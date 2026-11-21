namespace VideoUpscaler.Core.Upscaling;

/// <summary>
/// Modèle d'agrandissement d'une tuile. Le tampon d'entrée est au format planaire RVB
/// (3 plans de <c>width * height</c> valeurs 0..1) ; la sortie est au même format,
/// agrandie d'un facteur <see cref="Scale"/>.
/// </summary>
public interface IFrameUpscaler : IDisposable
{
    int Scale { get; }

    void Upscale(float[] input, int width, int height, float[] output);
}
