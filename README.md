# Upscale Studio 4K

Application Windows pour agrandir localement des vidéos avec Real-ESRGAN, jusqu'à la résolution
UHD 4K. Les modèles d'IA sont intégrés à l'application : aucune inscription ni service cloud n'est
nécessaire pour traiter une vidéo.

> Les fichiers vidéo restent sur votre PC. Le premier démarrage peut télécharger FFmpeg si aucun
> exemplaire compatible n'est déjà installé.

## Télécharger

Téléchargez la dernière version de **`UpscaleStudio4K.exe`** depuis la
[page des versions](https://github.com/maxlo245/ai-video-upscaler-4k/releases/latest).
L'exécutable Windows autonome inclut les modèles IA et ne nécessite pas l'installation de .NET.

## Fonctionnalités

- Interface Windows en français, sélection de fichiers, glisser-déposer et annulation.
- Agrandissement IA **2×** ou **4×**, avec une sortie plafonnée à **3840 × 2160** en paysage et
  **2160 × 3840** en portrait.
- Profil **Réaliste** avec Real-ESRGAN x4plus et profil **Animation** avec Real-ESRGAN x4plus
  Anime 6B.
- Inférence locale via ONNX Runtime : DirectML accélère le traitement sur les GPU compatibles
  NVIDIA, AMD et Intel ; le processeur sert de solution de repli.
- Barre de progression, temps écoulé et estimation actualisée du temps restant pour l'upscaling
  des images et l'encodage vidéo.
- Reconstruction en MP4 H.264/AAC, avec conservation de l'audio, des chapitres et des métadonnées
  de couleur lorsque la source les contient. Le tout **en flux continu** : aucun fichier image
  temporaire n'est écrit sur le disque.
- Qualité de sortie : **H.264 CRF 16, préréglage `slow`, `aq-mode=3`**, conversion RVB → YUV en
  Lanczos.

## Ce qui a changé dans le moteur (v0.5)

La version précédente fusionnait les tuiles sur 144 pixels, redimensionnait chaque tuile avec GDI+
puis arrondissait en 8 bits à chaque étape. Cela produisait des bandes légèrement floues, des
raccords décalés et une perte de micro-contraste. Le moteur a été réécrit :

1. **Tuilage sans couture** : chaque tuile est inférée avec 32 pixels de contexte miroir, puis sa
   zone utile est écrite une seule fois. Un fondu de 4 pixels seulement relie les tuiles voisines.
   Fini les bandes floues de 144 pixels.
2. **Accumulation en virgule flottante** : plus d'arrondi 8 bits entre deux tuiles, donc plus de
   bruit de quantification ni de lignes de raccord.
3. **Rééchantillonnage exact** : quand la sortie est plafonnée à 2×, le 4× du modèle est réduit par
   moyenne de surface exacte (et non par un bicubique GDI+ approximatif par tuile). Les autres
   facteurs utilisent un noyau Mitchell-Netravali calculé une fois par image.
4. **Pipeline 100 % en flux** : FFmpeg décode les images brutes, le moteur les traite, un second
   FFmpeg encode le MP4 en parallèle. Plus de fichiers PNG intermédiaires, plus de décodage GDI+,
   plus de demi-pixel de décalage, et plus besoin de réserver plusieurs dizaines de Go de disque.
5. **Tramage anti-banding** avant l'encodage, pour éviter les aplats dégradés.
6. **Trois réglages de qualité** : nettoyage de la source (hqdn3d), stabilité temporelle (atténue
   le scintillement des zones statiques) et fidélité (mélange la sortie IA avec la source pour
   réduire les détails inventés).
7. **Métadonnées préservées** : espace colorimétrique, plage, rotation, audio (copie si compatible
   MP4) et chapitres.

## Utilisation

1. Téléchargez et ouvrez `UpscaleStudio4K.exe`.
2. Sélectionnez une vidéo et l'emplacement de sortie.
3. Choisissez le facteur **2×** ou **4×**, le profil **Réaliste** ou **Animation**, puis les
   réglages de qualité.
4. Cliquez sur **LANCER L'UPSCALE**. Suivez l'avancement, le temps écoulé et le temps restant
   estimé dans l'application ; vous pouvez annuler le traitement.

Les modèles intégrés sont copiés dans `%LOCALAPPDATA%\UpscaleStudio4K\tools\models` et vérifiés
par SHA-256. Si FFmpeg et ffprobe ne sont pas trouvés dans le `PATH`, l'application les installe
dans son dossier local d'outils.

### Réglages de qualité

| Réglage | Valeurs | Effet |
|---|---|---|
| Nettoyage source | Aucun / Léger / Fort | Réduit les artefacts de compression avant l'IA. « Léger » est un bon compromis. |
| Stabilité temporelle | Désactivée / Légère / Forte | Lisse le scintillement des zones quasi immobiles. Trop forte, elle peut créer un léger traînée sur les mouvements lents. |
| Fidélité à la source | 100 % / 90 % / 75 % | À 100 %, l'IA décide de tout. En dessous, la sortie est mélangée à la source : moins de détails inventés, rendu plus « naturel ». |

## Configuration requise

- Windows 10 ou Windows 11 en 64 bits.
- Un GPU compatible DirectX 12 est recommandé. Sans accélération GPU, le traitement fonctionne
  sur le processeur, mais peut être nettement plus lent.
- Une connexion Internet est nécessaire au premier démarrage uniquement si FFmpeg doit être
  téléchargé.
- De l'espace disque pour le fichier de sortie uniquement : les images transitent en mémoire.

## Qualité et performances

Le gain effectif dépend du GPU, de la résolution et du contenu de la vidéo. Le moteur ne réduit
jamais la précision des modèles pour accélérer : il choisit simplement des tuiles plus petites si
la mémoire GPU est insuffisante.

Aucun modèle d'upscaling ne peut garantir zéro artefact. Les détails inventés par l'IA, les défauts
de la source et un léger scintillement temporel peuvent subsister. Le réglage de fidélité permet de
dosage ; une stabilisation temporelle agressive crée des traînées, c'est pourquoi elle reste
optionnelle.

Le temps restant est une estimation : il est recalculé selon la vitesse observée pendant
l'upscaling, puis selon l'avancement de l'encodage. L'extraction des images et l'installation de
FFmpeg ne sont pas incluses dans cette estimation.

## Limites connues

- Chaque image est traitée indépendamment : un léger scintillement peut apparaître entre les images,
  atténué par le réglage de stabilité temporelle.
- La sortie est un MP4 H.264/AAC 8 bits. Les sous-titres, pistes audio supplémentaires, métadonnées
  HDR et certaines métadonnées de chapitres ne sont pas conservés. Une source HDR est signalée dans
  le journal et convertie en SDR.
- Les vidéos à fréquence d'images variable sont converties à une fréquence constante basée sur la
  fréquence moyenne détectée ; un léger décalage audio/vidéo est possible.
- Les vidéos longues nécessitent du temps de calcul ; l'espace disque temporaire n'est plus utilisé.

## Construire depuis les sources

Installez le SDK .NET 10 et ouvrez PowerShell à la racine du dépôt :

```powershell
dotnet run --project .\VideoUpscaler.Core.Tests\VideoUpscaler.Core.Tests.csproj
dotnet run --project .\VideoUpscaler.App.Tests\VideoUpscaler.App.Tests.csproj
dotnet publish .\VideoUpscaler.App\VideoUpscaler.App.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --output .\artifacts `
  /p:PublishSingleFile=true `
  /p:IncludeNativeLibrariesForSelfExtract=true
```

L'exécutable est créé dans `artifacts\UpscaleStudio4K.exe`. Les poids des deux modèles sont
inclus directement dans le dépôt et incorporés à l'application ; Git LFS n'est pas nécessaire.

Les tests du moteur sont multiplateformes : ils vérifient le codec PNG, la géométrie des tuiles,
les poids de rééchantillonnage, la complémentarité des fondus, la continuité entre tuiles
(comparaison tuilée / non tuilée avec le vrai modèle), puis le pipeline vidéo en flux complet avec
FFmpeg s'il est installé.

## Modèles et licences

Les modèles Real-ESRGAN x4plus et x4plus Anime 6B sont distribués avec leurs informations de
provenance, empreintes et licences. Consultez
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) et [Models/RealESRGAN](Models/RealESRGAN).

## Projet de référence

L'interface reprend le principe d'une application de bureau légère autour d'un moteur IA local,
comme [AI Separator Studio](https://github.com/maxlo245/ai-separator-studio_public_stable).
Upscale Studio 4K est un projet distinct consacré à l'agrandissement vidéo.
