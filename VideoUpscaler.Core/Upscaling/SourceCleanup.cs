namespace VideoUpscaler.Core.Upscaling;

/// <summary>Préréglage de nettoyage appliqué à la source avant l'inférence.</summary>
public enum SourceCleanup
{
    None = 0,
    Light = 1,
    Strong = 2
}
