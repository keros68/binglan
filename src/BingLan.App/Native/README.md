# Native\BatteryEngine.dll

融合版可选组件：BatteryHub 读数引擎（GPL-3.0，源码在 BatteryHub 仓库 `engine/`，
构建脚本 `build-engine.sh`）。构建融合版时把产物 DLL 放到本目录，csproj 会随包复制；
缺失时外设电量模块按「引擎不可用」降级，其余功能不受影响。此 DLL 不进 git。
