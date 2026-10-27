namespace Switchknife.Facts.Bench;

/// <summary>
/// Synthetic embeddings with neighbours: points drawn around a fixed set of cluster centres, so a query's true nearest facts
/// are its cluster mates. Uniform random vectors have no neighbours, so recall on them would measure nothing.
/// <paramref name="Noise"/> is the spread around a centre relative to the centre's own spread: 0.5 is tight (cluster mates have
/// cosine about 0.8), 1.0 is loose (about 0.5), the hard case for a quantized scan because many mates are almost tied.
/// </summary>
internal sealed class VectorWorkload(int dimensions, int clusters, double noise, int seed)
{
    private readonly float[][] _centres = Enumerable.Range(0, clusters).Select(_ => new float[dimensions]).ToArray();
    private readonly Random _random = new(seed);
    private bool _centresDrawn;

    public float[] Next()
    {
        if (!_centresDrawn)
        {
            foreach (var centre in _centres)
            {
                for (var i = 0; i < centre.Length; i++)
                {
                    centre[i] = (float)Gaussian();
                }
            }

            _centresDrawn = true;
        }

        var around = _centres[_random.Next(_centres.Length)];
        var vector = new float[dimensions];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = around[i] + (float)(noise * Gaussian());
        }

        return vector;
    }

    private double Gaussian() => Math.Sqrt(-2 * Math.Log(1 - _random.NextDouble())) * Math.Cos(2 * Math.PI * _random.NextDouble());
}
