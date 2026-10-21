# Third-party components

- The [Real-ESRGAN x4plus model](https://huggingface.co/skillsafe-ai/realesrgan-x4plus)
  and [x4plus Anime 6B model](https://huggingface.co/skillsafe-ai/realesrgan-x4plus-anime-6b)
  are bundled in the application and repository. Their weights and architecture are from
  [Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN), under BSD-3-Clause, copyright
  Xintao Wang (2021). The complete model license is in
  [Models/RealESRGAN/LICENSE](Models/RealESRGAN/LICENSE). The ONNX conversions are pinned to
  the source revisions described in [Models/RealESRGAN/README.md](Models/RealESRGAN/README.md)
  and verified by SHA-256 during installation.
- [ONNX Runtime DirectML](https://github.com/microsoft/onnxruntime) is distributed under the
  MIT license. It provides local inference accelerated by compatible Windows GPUs.
- [FFmpeg](https://ffmpeg.org/), downloaded on first use as a Windows LGPL shared build from
  [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds). FFmpeg and included libraries
  have their own licenses and notices; see the downloaded archive and
  [FFmpeg's legal information](https://ffmpeg.org/legal.html).

FFmpeg is not included in this repository or the application executable.
