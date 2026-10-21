# Bundled Real-ESRGAN models

These model files are tracked with Git LFS and embedded in `UpscaleStudio4K.exe`. On first
use, the application extracts each model to `%LOCALAPPDATA%\UpscaleStudio4K\tools\models`
and verifies the recorded SHA-256 before loading it.

| Profile | Model | ONNX file | SHA-256 |
| --- | --- | --- | --- |
| Realistic | Real-ESRGAN x4plus, 23 RRDB blocks | `General/model.onnx` | `4851ec156207d271f5328605d0582eeb851e656227da8aca093ced9e60789291` |
| Anime | Real-ESRGAN x4plus Anime 6B | `Anime/model.onnx` | `9a029dae374b2cd4c3eb2dbbed857e22e6d785687c767aa12828e7356b9fb378` |

The ONNX exports and pinned source metadata are published by SkillSafe:

- [General model card, revision `107475e49b7d46412cc966481efccc3500955801`](https://huggingface.co/skillsafe-ai/realesrgan-x4plus/tree/107475e49b7d46412cc966481efccc3500955801)
  derives from [RealESRGAN_x4plus.pth](https://github.com/xinntao/Real-ESRGAN/releases/download/v0.1.0/RealESRGAN_x4plus.pth);
  upstream weights SHA-256: `4fa0d38905f75ac06eb49a7951b426670021be3018265fd191d2125df9d682f1`.
- [Anime model card, revision `c001be50fae1c7a2bae8b2b4dec5e6fcbdbcca96`](https://huggingface.co/skillsafe-ai/realesrgan-x4plus-anime-6b/tree/c001be50fae1c7a2bae8b2b4dec5e6fcbdbcca96)
  derives from [RealESRGAN_x4plus_anime_6B.pth](https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.2.4/RealESRGAN_x4plus_anime_6B.pth);
  upstream weights SHA-256: `f872d837d3c90ed2e05227bed711af5671a6fd1c9f7d7e91c911a61f155e99da`.

Both upstream model weights and architecture are licensed under BSD-3-Clause. See
[LICENSE](LICENSE) for the required notice and terms. The realistic model is the full
23-block x4plus model rather than the smaller General x4 v3 model. Both process video frames
individually; they do not provide temporal stabilization between adjacent frames.
