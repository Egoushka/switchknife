# /// script
# requires-python = ">=3.12"
# dependencies = ["graphiti-core==0.31.2", "httpx[socks]"]
# ///
"""Graphiti arm of the W3 retrieval bench: ingest the K0 corpus into Graphiti, answer the gold questions, time the searches.

Graphiti runs on its defaults: OpenAIClient, OpenAIEmbedder, Neo4j, ``Graphiti.search`` (hybrid search fused with RRF, 10 results).
Each fact is one text episode; the facts go in with ``add_episode_bulk`` in chunks of 50, oldest first. After the first chunk the
script prices the model calls it counted and stops when the projection for the whole corpus is above ``--cap``.

    OPENAI_API_KEY, OPENAI_BASE_URL   an OpenAI-compatible endpoint (read by the openai client; never written anywhere)
    NEO4J_URI, NEO4J_PASSWORD         a throwaway Neo4j 5.26 (user neo4j)

    uv run graphiti_arm.py ingest --corpus corpus.jsonl --state state.json [--cap 5]
    uv run graphiti_arm.py search --gold gold.jsonl --out graphiti.json

``search`` writes the file ``recall --external graphiti=FILE`` scores with the same matcher as the other arms.
"""

import argparse
import asyncio
import json
import os
import statistics
import sys
import time
from datetime import datetime

from graphiti_core import Graphiti
from graphiti_core.nodes import EntityNode, EpisodeType
from graphiti_core.utils.bulk_utils import RawEpisode

CHUNK = 50
# The gateway key allows 120 requests a minute and other services share it; Graphiti's bulk add fires more than that.
REQUESTS_PER_MINUTE = int(os.environ.get("REQUESTS_PER_MINUTE", "80"))


class Pacer:
    """Spaces model calls evenly so the whole process stays under REQUESTS_PER_MINUTE."""

    def __init__(self, per_minute: int) -> None:
        self.gap = 60.0 / per_minute
        self.next = 0.0
        self.lock = asyncio.Lock()

    async def wait(self) -> None:
        async with self.lock:
            now = time.monotonic()
            slot = max(now, self.next)
            self.next = slot + self.gap
        await asyncio.sleep(slot - now)


class Meter:
    """Counts the tokens of every model call the openai clients make, from the responses' own usage."""

    def __init__(self, price: dict | None = None) -> None:
        self.calls = {"llm": 0, "embed": 0}
        self.tokens = {"llm_in": 0, "llm_cached": 0, "llm_out": 0, "embed": 0}
        self.pacer = Pacer(REQUESTS_PER_MINUTE)
        self.price = price

    def wrap(self, graphiti: Graphiti) -> None:
        llm = graphiti.llm_client.client
        # The embedder has no retry of its own; the SDK's backoff honours the gateway's retry-after.
        llm.max_retries = graphiti.embedder.client.max_retries = 8
        parse, create = llm.responses.parse, llm.chat.completions.create
        embed = graphiti.embedder.client.embeddings.create

        async def counted_parse(*a, **k):
            await self.pacer.wait()
            r = await parse(*a, **k)
            self._llm(r.usage.input_tokens, r.usage.output_tokens, getattr(getattr(r.usage, "input_tokens_details", None), "cached_tokens", 0) or 0)
            return r

        async def counted_create(*a, **k):
            await self.pacer.wait()
            r = await create(*a, **k)
            self._llm(r.usage.prompt_tokens, r.usage.completion_tokens, 0)
            return r

        async def counted_embed(*a, **k):
            await self.pacer.wait()
            r = await embed(*a, **k)
            self.calls["embed"] += 1
            self.tokens["embed"] += r.usage.total_tokens
            self._progress()
            return r

        llm.responses.parse, llm.chat.completions.create = counted_parse, counted_create
        graphiti.embedder.client.embeddings.create = counted_embed

    def _llm(self, tokens_in: int, tokens_out: int, cached: int) -> None:
        self.calls["llm"] += 1
        self.tokens["llm_in"] += tokens_in
        self.tokens["llm_out"] += tokens_out
        self.tokens["llm_cached"] += cached
        self._progress()

    def _progress(self) -> None:
        # A chunk can take longer than a session lives; this line keeps the spend known if the process is killed.
        if sum(self.calls.values()) % 50 == 0:
            print(f"meter: {self.calls} {self.tokens} {f"${self.usd(self.price):.4f}" if self.price else ""}", flush=True)

    def usd(self, price: dict) -> float:
        # Cached input is priced as full input: the projection errs high.
        return (self.tokens["llm_in"] * price["llm_in"] + self.tokens["llm_out"] * price["llm_out"] + self.tokens["embed"] * price["embed"]) / 1e6

    def snapshot(self, price: dict) -> dict:
        return {"calls": dict(self.calls), "tokens": dict(self.tokens), "usd": round(self.usd(price), 4)}


def graphiti() -> Graphiti:
    # Graphiti reads this when it sends an event, not at import.
    os.environ.setdefault("GRAPHITI_TELEMETRY_ENABLED", "false")
    return Graphiti(os.environ.get("NEO4J_URI", "bolt://localhost:7687"), "neo4j", os.environ["NEO4J_PASSWORD"])


def episodes(corpus: str) -> list[RawEpisode]:
    rows = [json.loads(line) for line in open(corpus, encoding="utf-8") if line.strip()]
    # The export has no fact id: order by source time, then text, and name episodes by that position.
    rows.sort(key=lambda r: (r["sources"][0]["occurred_at"], r["text"]))
    return [
        RawEpisode(
            name=f"fact {n}",
            content=f"{r['subject']['name']} ({r['subject']['kind']}): {r['text']}",
            source_description=f"{r['sources'][0]['kind']}: {r['sources'][0]['ref']}",
            source=EpisodeType.text,
            reference_time=datetime.fromisoformat(r["sources"][0]["occurred_at"]),
        )
        for n, r in enumerate(rows)
    ]


async def ingest(args: argparse.Namespace) -> int:
    price = {"llm_in": args.price_in, "llm_out": args.price_out, "embed": args.price_embed}
    state = json.load(open(args.state)) if os.path.exists(args.state) else {"done": [], "chunks": []}
    all_episodes = episodes(args.corpus)
    g = graphiti()
    meter = Meter(price)
    meter.wrap(g)
    try:
        if not state["done"]:
            await g.build_indices_and_constraints()
        for start in range(0, len(all_episodes), CHUNK):
            if start in state["done"]:
                continue
            chunk = all_episodes[start:start + CHUNK]
            # A killed run leaves this chunk's episodes saved and its entities not yet (the bulk add saves those last): drop them so a
            # rerun adds each fact once.
            await g.driver.execute_query("MATCH (e:Episodic) WHERE e.name IN $names DETACH DELETE e", names=[e.name for e in chunk])
            began = time.monotonic()
            await g.add_episode_bulk(chunk)
            state["done"].append(start)
            state["chunks"].append({"start": start, "seconds": round(time.monotonic() - began, 1), **meter.snapshot(price)})
            json.dump(state, open(args.state, "w"), indent=2)
            facts = len(state["done"]) * CHUNK
            spent = meter.usd(price)
            print(f"chunk {start}: {facts} facts in, ${spent:.4f} this process, {meter.calls}", flush=True)
            if len(state["chunks"]) == 1:
                projected = spent * len(all_episodes) / min(CHUNK, len(all_episodes))
                state["projection"] = {"after_facts": CHUNK, "usd": round(spent, 4), "projected_usd": round(projected, 4), "cap_usd": args.cap}
                json.dump(state, open(args.state, "w"), indent=2)
                print(f"projection: ${projected:.2f} for {len(all_episodes)} facts (cap ${args.cap:.2f})", flush=True)
                if projected > args.cap:
                    print("STOP: projection above the cap", flush=True)
                    return 4
        return 0
    finally:
        await g.close()


async def search(args: argparse.Namespace) -> int:
    gold = [json.loads(line) for line in open(args.gold, encoding="utf-8") if line.strip()]
    g = graphiti()
    meter = Meter()
    meter.wrap(g)
    # Query vectors computed once, like recall's: the warm-up pass fills the cache, the timed passes reuse it.
    embed = g.embedder.create
    cache: dict[str, list[float]] = {}

    async def cached(input_data, *a, **k):
        key = json.dumps(input_data)
        if key not in cache:
            cache[key] = await embed(input_data, *a, **k)
        return cache[key]

    g.embedder.create = cached
    try:
        results = []
        for d in gold:
            edges = await g.search(d["question"], num_results=args.limit)
            uuids = list({u for e in edges for u in (e.source_node_uuid, e.target_node_uuid)})
            names = {n.uuid: n.name for n in await EntityNode.get_by_uuids(g.driver, uuids)} if uuids else {}
            shown = [f"{names.get(e.source_node_uuid, '')}\n{e.fact}\n{names.get(e.target_node_uuid, '')}" for e in edges]
            results.append({"id": d["id"], "shown": shown, "names": [n for e in edges for n in (names.get(e.source_node_uuid, ''), names.get(e.target_node_uuid, ''))]})
        timings = []
        for _ in range(10):
            for d in gold:
                t = time.perf_counter()
                await g.search(d["question"], num_results=args.limit)
                timings.append((time.perf_counter() - t) * 1000)
        timings.sort()
        p95 = timings[int(round(0.95 * (len(timings) - 1)))]
        json.dump({"system": "graphiti 0.31.2 (defaults, Neo4j 5.26)", "results": results, "timings_ms": timings, "search_model_calls": meter.calls}, open(args.out, "w"), indent=2)
        print(f"graphiti: {len(timings)} timings, p50 {statistics.median(timings):.1f} ms, p95 {p95:.1f} ms; model calls during search {meter.calls}")
        return 0
    finally:
        await g.close()


def main() -> int:
    p = argparse.ArgumentParser()
    sub = p.add_subparsers(dest="verb", required=True)
    i = sub.add_parser("ingest")
    i.add_argument("--corpus", required=True)
    i.add_argument("--state", required=True)
    i.add_argument("--cap", type=float, default=5.0)
    # USD per million tokens; the defaults are starting values, pass the endpoint's own prices.
    i.add_argument("--price-in", type=float, default=0.10)
    i.add_argument("--price-out", type=float, default=0.50)
    i.add_argument("--price-embed", type=float, default=0.02)
    s = sub.add_parser("search")
    s.add_argument("--gold", required=True)
    s.add_argument("--out", required=True)
    s.add_argument("--limit", type=int, default=10)
    args = p.parse_args()
    return asyncio.run(ingest(args) if args.verb == "ingest" else search(args))


if __name__ == "__main__":
    sys.exit(main())
