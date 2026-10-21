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
  NVIDIA, AMD et Intel; le processeur sert de solution de repli.
- Barre de progression, temps écoulé et estimation actualisée du temps restant pour l'upscaling
  des images et l'encodage vidéo.
- Reconstruction en MP4 H.264/AAC, avec conservation d'une piste audio lorsque la vidéo en contient.
- Fusion progressive des zones de chevauchement entre tuiles afin d'éviter les lignes de raccord,
  sans réduire la précision des modèles.
- Encodage H.264 en qualité élevée (CRF 16) avec un préréglage rapide.

## Utilisation

1. Téléchargez et ouvrez `UpscaleStudio4K.exe`.
2. Sélectionnez une vidéo et l'emplacement de sortie.
3. Choisissez le facteur **2×** ou **4×** ainsi que le profil **Réaliste** ou **Animation**.
4. Cliquez sur **LANCER L'UPSCALE**. Suivez l'avancement, le temps écoulé et le temps restant
   estimé dans l'application; vous pouvez annuler le traitement.

Les modèles intégrés sont copiés dans `%LOCALAPPDATA%\UpscaleStudio4K\tools\models` et vérifiés
par SHA-256. Si FFmpeg et ffprobe ne sont pas trouvés dans le `PATH`, l'application les installe
dans son dossier local d'outils.

## Configuration requise

- Windows 10 ou Windows 11 en 64 bits.
- Un GPU compatible DirectX 12 est recommandé. Sans accélération GPU, le traitement fonctionne
  sur le processeur, mais peut être nettement plus lent.
- Une connexion Internet est nécessaire au premier démarrage uniquement si FFmpeg doit être
  téléchargé.
- De l'espace disque temporaire suffisant : les images sources et agrandies sont préparées avant
  l'assemblage de la vidéo et peuvent occuper plusieurs fois la taille du fichier d'origine.

## Qualité et performances

Les modèles et leur précision ne sont pas réduits pour accélérer le traitement. L'application
utilise des tuiles plus larges avec chevauchement pour limiter le nombre d'inférences et évite un
redimensionnement inutile lorsque la sortie est déjà à la taille produite par le modèle. Le gain
effectif dépend du GPU, de la résolution et du contenu de la vidéo.

Le traitement réduit les artefacts de raccord entre tuiles et la compression, mais aucun modèle
d'upscaling ne peut garantir zéro artefact : les détails inventés par l'IA, les défauts de la source
et le scintillement temporel peuvent subsister. Une stabilisation temporelle agressive peut créer
des traînées (ghosting); elle n'est donc pas appliquée automatiquement.

Le temps restant est une estimation : il est recalculé selon la vitesse observée pendant
l'upscaling, puis selon l'avancement de l'encodage. L'extraction des images et l'installation de
FFmpeg ne sont pas incluses dans cette estimation.

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
inclus directement dans le dépôt et incorporés à l'application; Git LFS n'est pas nécessaire.

## Limites connues

- Chaque image est traitée indépendamment : un léger scintillement peut apparaître entre les images.
- La sortie est un MP4 H.264/AAC 8 bits. Les sous-titres, pistes audio supplémentaires, métadonnées
  HDR et certaines métadonnées de chapitres ne sont pas conservés.
- Les vidéos à fréquence d'images variable sont converties à une fréquence constante basée sur la
  fréquence moyenne détectée; un léger décalage audio/vidéo est possible.
- Les vidéos longues nécessitent du temps de calcul et de l'espace disque temporaire.

## Modèles et licences

Les modèles Real-ESRGAN x4plus et x4plus Anime 6B sont distribués avec leurs informations de
provenance, empreintes et licences. Consultez
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) et [Models/RealESRGAN](Models/RealESRGAN).

## Projet de référence

L'interface reprend le principe d'une application de bureau légère autour d'un moteur IA local,
comme [AI Separator Studio](https://github.com/maxlo245/ai-separator-studio_public_stable).
Upscale Studio 4K est un projet distinct consacré à l'agrandissement vidéo.
