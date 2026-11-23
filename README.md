# Moment Frames

Moment Frames is a RimWorld mod that automatically photographs weddings, celebrations, battles, and other colony milestones. Arrange portraits, revisit your photos in the album, hang a favorite on the wall, or show your colony a slideshow.

[Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3835409869) · [Download v2.6.0](https://github.com/rucy74/Rimworld-Moment-Frames/archive/refs/tags/v2.6.0.zip)

## Install

1. Install [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077) for RimWorld 1.6 or 1.7.
2. Extract the download into RimWorld's `Mods` folder. The extracted mod folder should contain `About/About.xml`.
3. In RimWorld, enable Harmony and Moment Frames. If you already use the Workshop copy, enable just one copy of Moment Frames.

In v2.6.0, new photos default to JPG. You can choose lossless PNG in the mod settings. Existing PNGs stay as they are. To delete a photo from disk, remove it from the album, then delete it in the Archive tab. Colonies share their photo folder across saves, so an older save may also lose access to a photo you delete.

## Build the source

The playable mod is ready to use from the download. To build it, use the .NET SDK with the RimWorld 1.6 managed assemblies and the 1.6 build of Harmony. The shared mod DLL also supports RimWorld 1.7 through its compatibility layer.

```powershell
dotnet build Source/MomentFrames.csproj -c Debug "-p:GameRoot=C:/path/to/RimWorld-1.6" "-p:HarmonyPath=C:/path/to/0Harmony.dll"
```

The build writes `Assemblies/MomentFrames.dll`. The font notices are included in [Fonts](Fonts/).

## 한국어

결혼식, 축제, 전투처럼 정착지의 중요한 순간을 자동으로 사진에 남기는 림월드 모드예요. 단체 사진을 직접 찍거나, 앨범을 보고, 액자와 환등기로 사진을 감상할 수 있어요.

림월드 1.6과 1.7을 지원하고 Harmony가 필요해요. 위 ZIP을 림월드의 `Mods` 폴더에 풀면 됩니다. 창작마당에서 이미 구독했다면 두 사본 중 하나만 켜 주세요.

2.6.0부터 새 사진은 기본 JPG로 저장돼요. 설정에서 무손실 PNG를 고를 수 있고, 기존 PNG 사진은 그대로예요. 사진 파일까지 지우려면 앨범에서 뺀 뒤 보관함에서 삭제하세요. 같은 정착지의 이전 세이브도 이 사진을 열 수 없게 될 수 있어요.
