namespace VideoUpscaler.Core.Upscaling;

/// <summary>Réglages de qualité du moteur d'agrandissement.</summary>
public sealed record UpscaleOptions
{
    /// <summary>Taille de la zone utile d'une tuile, en pixels source.</summary>
    public int TileSize { get; init; } = 384;

    /// <summary>Marge de contexte autour de chaque tuile, en pixels source.</summary>
    public int TilePadding { get; init; } = 32;

    /// <summary>Largeur du fondu entre deux tuiles voisines, en pixels source.</summary>
    public int TileFeather { get; init; } = 4;

    /// <summary>
    /// Part de détail produit par l'IA (1 = sortie du modèle, 0 = interpolation classique).
    /// Une valeur légèrement inférieure à 1 réduit les détails inventés et le scintillement.
    /// </summary>
    public double Fidelity { get; init; } = 1.0;

    /// <summary>
    /// Lissage temporel des zones quasi statiques (0 = désactivé, 1 = maximal).
    /// Atténue le scintillement sans créer de traînées sur les mouvements rapides.
    /// </summary>
    public double TemporalStability { get; init; } = 0.0;

    public UpscaleOptions Validate()
    {
        if (TileSize is < 8 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(TileSize), "La tuile doit être comprise entre 8 et 1024 pixels.");
        if (TilePadding is < 0 or > 256)
            throw new ArgumentOutOfRangeException(nameof(TilePadding), "La marge doit être comprise entre 0 et 256 pixels.");
        if (TileFeather is < 0 or > 64)
            throw new ArgumentOutOfRangeException(nameof(TileFeather), "Le fondu doit être compris entre 0 et 64 pixels.");
        if (TileFeather > TilePadding)
            throw new ArgumentOutOfRangeException(nameof(TileFeather), "Le fondu ne peut pas dépasser la marge de contexte.");
        if (!double.IsFinite(Fidelity) || Fidelity is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(Fidelity), "La fidélité doit être comprise entre 0 et 1.");
        if (!double.IsFinite(TemporalStability) || TemporalStability is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(TemporalStability), "La stabilité doit être comprise entre 0 et 1.");
        return this;
    }
}
