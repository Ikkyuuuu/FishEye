FishEyes C# dependency notices

Chess.com piece artwork:
  Recognition templates and Neo preview pieces use public Chess.com sprites.
  Artwork belongs to Chess.com and its respective creators, and is not
  covered by the MIT/Apache notices below. Source URLs and image hashes:
  assets/themes/manifest.json in the source repository.
  https://www.chess.com/

Recognition model and preprocessing conventions:
  Harshit Pawar / chessvision (MIT)
  https://github.com/harshitpawar64/chessvision
  https://huggingface.co/harshitpawar64/chessvision

Microsoft ONNX Runtime 1.30.0 (MIT):
  https://github.com/microsoft/onnxruntime
  Includes its upstream ThirdPartyNotices.txt.

OpenCvSharp 4.13.0.20260627 and OpenCV 4.13.0 (Apache-2.0):
  https://github.com/shimat/opencvsharp
  https://github.com/opencv/opencv

.NET and Windows Forms (MIT):
  https://github.com/dotnet/runtime
  https://github.com/dotnet/winforms

Stockfish 19 (GPL-3.0):
  https://github.com/official-stockfish/Stockfish/releases/tag/sf_19
  The unmodified Windows x86-64 universal distribution is embedded in FishEyes.
  It includes the executable, GPLv3 license, corresponding source and build docs.
  On first use these are extracted under %LOCALAPPDATA%\FishEyes\engines.
  License: Stockfish-GPL-3.0.txt
  Exact archive URL and checksums: assets/engine/stockfish.json in this repository.
  Embedded evaluation networks can be obtained as described in the upstream
  src/Makefile and README. The upstream distribution is available at:
  https://github.com/official-stockfish/Stockfish/releases/download/sf_19/stockfish-windows-x86-64-universal.zip

FishEyes' C# grid detector is a new implementation. The ONNX classifier uses
the original model's class metadata and normalization values. This package
contains unmodified third-party binaries and the unmodified recognition model.
