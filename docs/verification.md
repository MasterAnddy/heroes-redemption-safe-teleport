# 驗證記錄

本頁記錄 `v1.0.0` 公開發行包的可重現建置與受控 fixture。測試只讀取指定遊戲目錄以取得建置參考組件與校驗目標，沒有寫入遊戲檔案、存檔或運行中的遊戲程序。

## 測試環境與輸入

- Windows x64
- .NET SDK 8
- BepInEx 6 IL2CPP interop 已生成
- `GameAssembly.dll` SHA-256：
  `56584F8D7E96FDB3716EC00E5FB27238A53CD2E92D6150B8F1F435EAA7453541`

完整命令：

```powershell
.\Build.ps1 -GameRoot '<game-root>'
```

結果：退出狀態 `0`，插件及 Live 工具皆為 `0 warnings / 0 errors`，建置最後輸出 `STATUS=PASS`。

## BepInEx 檢查點策略

基準命令：

```powershell
dotnet run --project tests/SafeTeleportFixture/SafeTeleportFixture.csproj --configuration Release -- --baseline
```

相關輸出（退出狀態 `0`）：

```text
MODE=baseline
OUTPUT=teleported=false destination=none autoCheckpoints=0
CHECKS=2
STATUS=PASS
EXIT_STATUS=0
```

修改後命令：

```powershell
dotnet run --project tests/SafeTeleportFixture/SafeTeleportFixture.csproj --configuration Release
```

相關輸出（退出狀態 `0`）：

```text
MODE=modified
OUTPUT=autoDestination=(0,0) manualDestination=(7,3) boundedCount=4 nearestBounded=(11.3,2)
SPAWN_FALLBACK=OldPrison(-134.8125,-105.625)
GUARDS=damage:true pauseRecording:true deathRecording:true sceneReset:true playerReset:true postTeleportBlock:true
CHECKS=26
STATUS=PASS
EXIT_STATUS=0
```

這組 fixture 覆蓋可靠性延遲、受傷／暫停／死亡保護、手動位置優先、場景及玩家實例隔離、瞬移後短暫禁止重新記錄，以及自動歷史數量上限。

## Live 鉤子與回滾 fixture

基準命令：

```powershell
HeroesRedemption.SafeTeleportLive.exe --fixture-baseline --config=safe-teleport-config.json
```

相關輸出（退出狀態 `0`）：

```text
FIXTURE mode=baseline input=(12.5,-3.25) original=40534883EC3080796000488BD9
FIXTURE F7=NO_HANDLER F6=NO_HANDLER before=(12.5, -3.25) after=(12.5, -3.25) moved=0
STATUS=PASS EXIT_STATUS=0
```

修改後命令：

```powershell
HeroesRedemption.SafeTeleportLive.exe --fixture --config=safe-teleport-config.json
```

相關輸出（退出狀態 `0`）：

```text
FIXTURE mode=modified input=(12.5,-3.25) original=40534883EC3080796000488BD9
FIXTURE hookAddress=0x12345601000 codeBytes=352 patch=48B80010604523010000FFE090
FIXTURE F7=(12.5, -3.25) F6=(12.5, -3.25) source=manual-anchor moved=1 velocityAfter=(0,0)
FIXTURE sceneChange=0x22220000 anchorCleared=1 scene=Cemetery verifiedSpawn=(-123.5, -1.125) emergency1=(1, 6) emergency2=(5, 2)
FIXTURE rollback=40534883EC3080796000488BD9 exact=1
STATUS=PASS EXIT_STATUS=0
```

fixture 證明 13-byte 入口跳板可辨識，場景切換會清除錨點，F6 會使用手動錨點或場景出生點，速度歸零，最後逐 byte 還原入口。

## Live 靜態目標校驗

命令：

```powershell
HeroesRedemption.SafeTeleportLive.exe --validate --config=safe-teleport-config.json --game-root='<game-root>'
```

相關輸出（退出狀態 `0`）：

```text
moduleHash=PASS prologue=PASS patchLength=13 hookCodeBytes=352 status=PASS
```

這項校驗讀取完整 `GameAssembly.dll` SHA-256 與 `PlayerStats.Update` 原始 13 bytes。公開發行包建置期間未對運行中的程序套用 Live 鉤子。

## 發行包內容與隱私檢查

兩個 ZIP 均重新開啟列舉，確認只包含本專案的最小運行檔案、設定、說明、授權、manifest 及回滾腳本。檢查結果：

```text
BINARY_PATH_HITS=0
TEXT_PATH_HITS=0
DIST_FORBIDDEN_ENTRIES=0
MANIFEST_FAILURES=0
SHA256SUM_FAILURES=0
```

掃描同時檢查插件 DLL 與 Live EXE 的 ASCII／UTF-16 字串，未發現本機磁碟、使用者名稱、暫存目錄或建置目錄。發行包不含 PDB、執行日誌、PID、現場地址、遊戲二進制、存檔或其他模組。壓縮包雜湊的權威清單位於根目錄 [`SHA256SUMS`](../SHA256SUMS)。

解壓後另以只含目標雜湊與空白 `BepInEx` 目錄的 fixture 執行安裝及回滾；安裝 DLL 與包內 payload 雜湊相同，回滾後 DLL 與安裝狀態檔皆不存在。再從重新解壓的 Live ZIP 執行修改後 fixture。相關結果（退出狀態 `0`）：

```text
INSTALL_READBACK_STATUS=PASS
RESTORED_PLUGIN=False
RESTORED_CONFIG=False
ROLLBACK_READBACK_STATUS=PASS
FIXTURE_GAME_BINARY_REMOVED=True
REOPEN_INSTALL_ROLLBACK_STATUS=PASS
```

## 回滾界線

- BepInEx 包的 `Install.ps1` 在覆蓋前保存既有插件與設定；`Rollback.ps1` 校驗現場 DLL 後恢復備份。
- Live 包的 F12／`Rollback.ps1` 只識別帶 `HRTPSAFE` 魔數與版本的入口。未知入口會停止覆蓋。
- Live 入口先在執行緒暫停並確認 RIP 不在修改區間後才寫入或恢復；入口恢復後，只有在 active-depth 歸零且執行緒不在分配區時才釋放該區塊。
