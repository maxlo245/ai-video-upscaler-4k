# Third-party components

The application downloads these third-party command-line tools on first use; they are not
included in this repository or in the application executable:

- [Real-ESRGAN ncnn Vulkan](https://github.com/xinntao/Real-ESRGAN-ncnn-vulkan), distributed
  under its upstream BSD-3-Clause license.
- The `RealESRGAN_General_x4_v3` and `realesr-animevideov3-x4` model files are downloaded from
  [Upscayl custom models](https://github.com/upscayl/custom-models) at commit
  `4b6d2cfa59c7442af115dfc6e50fd8d7d40b96ef`. That repository credits the Real-ESRGAN project;
  the original [Real-ESRGAN project](https://github.com/xinntao/Real-ESRGAN) is MIT licensed.
  Model files are fetched at runtime and are not included in this repository or executable.
- [FFmpeg](https://ffmpeg.org/), downloaded as a Windows LGPL shared build from
  [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds). FFmpeg and included libraries
  have their own licenses and notices; see the downloaded archive and
  [FFmpeg's legal information](https://ffmpeg.org/legal.html).

Upscale Studio 4K does not bundle or redistribute these binaries. Their URLs and licenses are
controlled by their respective upstream projects.
