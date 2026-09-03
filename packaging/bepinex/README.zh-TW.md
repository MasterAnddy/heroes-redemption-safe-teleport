# SafeTeleport：BepInEx 持久插件

這個版本隨遊戲啟動，自動記錄玩家走過且經過可靠性延遲的位置，供卡位時返回。

## 安裝

需求：遊戲已安裝 BepInEx 6 IL2CPP，且至少成功啟動過一次以產生 `BepInEx/interop`。

在 PowerShell 進入解壓後的目錄：

```powershell
$GameRoot = Read-Host '請輸入遊戲目錄'
.\Install.ps1 -GameRoot $GameRoot
```

安裝完成後啟動遊戲。

## 按鍵

- **F6**：返回手動檢查點；沒有手動檢查點時，返回最近的可靠自動檢查點。若本局還沒有可靠紀錄，使用已知場景的出生點。
- **F7**：保存目前位置為本地圖的手動檢查點。

切換地圖或建立新的玩家實例時，所有檢查點都會清空。瞬移會同步 `Transform` 與 `Rigidbody2D`，並清除剛體速度。

設定檔位於：

```text
BepInEx/config/local.heroesredemption.safeteleport.cfg
```

## 回滾

關閉遊戲後執行：

```powershell
$GameRoot = Read-Host '請輸入遊戲目錄'
.\Rollback.ps1 -GameRoot $GameRoot
```

回滾腳本會校驗已安裝 DLL，並還原安裝前保存的插件與設定。若安裝後修改過新設定，腳本會保留現場並停止刪除。

本套件只支援 `manifest.json` 所列的 `GameAssembly.dll` SHA-256。
