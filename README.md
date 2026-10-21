# Upscale Studio 4K

Application Windows de bureau pour agrandir une vidéo avec une IA locale, jusqu'au 4K UHD.
Le projet reprend l'idée d'une interface graphique légère autour d'un moteur dédié de votre
application AI Separator Studio, sans reprendre son code. Les vidéos ne sont pas téléversées :
les traitements et les modèles tournent sur votre PC.

## Fonctionnalités

- Interface Windows en français, avec sélection de fichier, glisser-déposer et annulation.
- Upscaling vidéo **2×** ou **4×**, limité à **3840 × 2160** en paysage et **2160 × 3840** en portrait.
- Modèle Real-ESRGAN généraliste ou anime, téléchargé depuis un instantané amont épinglé.
- Extraction des images, inférence IA, puis reconstruction MP4 avec audio AAC.
- Téléchargement guidé de Real-ESRGAN et, si nécessaire, de FFmpeg LGPL lors du premier traitement.
- Exécutable Windows autonome publié dans les Releases GitHub.

## Configuration

- Windows 10 ou 11 en 64 bits.
- Connexion Internet au premier usage pour télécharger Real-ESRGAN et, si aucun FFmpeg n'est
  disponible dans le `PATH`, une version FFmpeg LGPL.
- Carte graphique Vulkan compatible recommandée. L'application tire parti du matériel compatible
  NVIDIA/AMD/Intel via Real-ESRGAN ncnn Vulkan; les performances dépendent de la carte graphique,
  du codec et de la durée de la vidéo.
- Espace disque temporaire : les images extraites et agrandies peuvent demander plusieurs fois
  la taille de la vidéo d'origine. Les fichiers temporaires sont supprimés après chaque traitement.

## Télécharger et utiliser

1. Téléchargez `UpscaleStudio4K.exe` depuis les
   [Releases privées](https://github.com/maxlo245/ai-video-upscaler-4k/releases) (un accès au
   dépôt GitHub est nécessaire).
2. Lancez l'exécutable et choisissez une vidéo MP4, MKV, MOV, WebM, AVI, M4V ou WMV.
3. Choisissez 2× ou 4×, puis le profil réaliste ou animation.
4. Cliquez sur **LANCER L'UPSCALE**. Au premier lancement, l'application télécharge les outils
   nécessaires. Votre GPU traite ensuite les images localement.

Les binaires et modèles tiers sont installés dans `%LOCALAPPDATA%\UpscaleStudio4K\tools`.
La première installation de FFmpeg peut télécharger un paquet d'environ 80 Mo.

## Construire l'exécutable

Installez le SDK .NET 10, puis lancez PowerShell à la racine du dépôt :

```powershell
dotnet run --project .\VideoUpscaler.Core.Tests\VideoUpscaler.Core.Tests.csproj
dotnet publish .\VideoUpscaler.App\VideoUpscaler.App.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --output .\artifacts `
  /p:PublishSingleFile=true `
  /p:IncludeNativeLibrariesForSelfExtract=true
```

Le fichier final se trouve dans `artifacts\UpscaleStudio4K.exe`. La CI publie également un
exécutable Windows dans chaque release créée à partir d'une étiquette `v*`.

## Limites de cette première version

- L'IA traite chaque image indépendamment. Certains détails peuvent donc scintiller entre les
  images; le rendu dépend beaucoup de la vidéo d'origine.
- Le format de sortie est MP4 H.264/AAC 8 bits. Les sous-titres, plusieurs pistes audio, les
  métadonnées HDR et une partie des métadonnées de chapitre ne sont pas conservés.
- La sortie est à fréquence d'images constante, dérivée de la fréquence moyenne détectée. Les
  vidéos à fréquence d'images variable peuvent présenter un léger décalage audio/vidéo.
- Le traitement de vidéos longues peut être lent et exige beaucoup d'espace temporaire. Cette
  version prépare les images de tout le clip avant de lancer l'inférence.
- Les exécutables et modèles IA sont téléchargés depuis leurs dépôts amont. Consultez
  [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) et les conditions de leurs auteurs.

## Référence

L'interface suit le principe de votre application
[AI Separator Studio](https://github.com/maxlo245/ai-separator-studio_public_stable) :
une application Windows autonome qui guide l'utilisateur et lance un moteur IA local. Le projet
est une implémentation distincte consacrée à l'upscaling vidéo.
