# Heroes & Redemption Safe Teleport

《Heroes & Redemption》的離線單機卡位救援工具。按 **F6** 返回可靠位置，按 **F7** 保存手動位置；瞬移時會清除角色剛體速度，避免角色再次被推回碰撞區。

本倉庫提供兩種發行包：

| 發行包 | 適用情境 | BepInEx | 主要按鍵 |
|---|---|---:|---|
| `HeroesRedemption-SafeTeleport-BepInEx.zip` | 長期使用，隨遊戲啟動並自動建立可靠檢查點 | 需要 | F6、F7 |
| `HeroesRedemption-SafeTeleport-Live.zip` | 遊戲已啟動且角色當下卡住 | 不需要 | F6、F7、F8、F12 |

兩個版本擇一使用；不要同時啟動，否則相同的 F6/F7 熱鍵會被兩邊處理。下載後以根目錄的 `SHA256SUMS` 核對壓縮包。

## 支援版本

程式會在動作前校驗 `GameAssembly.dll`：

```text
56584F8D7E96FDB3716EC00E5FB27238A53CD2E92D6150B8F1F435EAA7453541
```

雜湊不同代表遊戲版本不符，工具會停止。這個限制可防止固定類型或函式位置被套用到未知版本。

## 使用方式

### BepInEx 持久插件

1. 安裝 BepInEx 6 IL2CPP，並至少啟動一次遊戲以產生 `BepInEx/interop`。
2. 解壓 BepInEx 發行包。
3. 關閉遊戲，在 PowerShell 執行：

```powershell
$GameRoot = Read-Host '請輸入遊戲目錄'
.\Install.ps1 -GameRoot $GameRoot
```

進入地圖後，插件只會把玩家存活、未暫停且經過可靠性延遲的位置加入歷史。切圖或玩家實例改變時會清空舊記錄。

### Live 即時工具

1. 安裝 .NET 8 Runtime，解壓 Live 發行包。
2. 遊戲進入地圖後執行 `Start-SafeTeleport.cmd`。
3. 切回遊戲，按 **F6** 脫困；到達安全位置後按 **F7** 保存。
4. 完成後按 **F12** 還原即時鉤子並退出工具。

Live 版只修改目前遊戲程序的記憶體，不修改磁碟上的遊戲組件。F12 會先還原入口；確認沒有執行中的鉤子後釋放配置，未立即釋放的區塊會由遊戲程序結束時回收。若工具異常退出而遊戲仍在運行，可執行包內 `Rollback.ps1`。

## 從原始碼建置

需求：Windows PowerShell、.NET 8 SDK，以及一份已產生 BepInEx IL2CPP interop 的遊戲目錄。

```powershell
.\Build.ps1 -GameRoot (Read-Host '請輸入遊戲目錄')
```

建置腳本會：

1. 校驗目標遊戲組件；
2. 建置持久插件與 Live 工具；
3. 執行兩組基準／修改後 fixture 及 Live 靜態驗證；
4. 生成 `dist/` 內的兩個最小發行包與 `SHA256SUMS`。

建置不會複製或封裝任何遊戲二進制。詳細測試方式見 [`docs/verification.md`](docs/verification.md)。

## 專案結構

```text
src/HeroesRedemption.SafeTeleport/  BepInEx IL2CPP 插件
src/SafeTeleportLive/               Windows 即時工具
tests/SafeTeleportFixture/          Unity 無關的檢查點策略 fixture
packaging/                           安裝、設定與回滾檔案
dist/                                可發布壓縮包
```

## 邊界

- 僅供離線單機使用。
- 不包含遊戲二進制、存檔或第三方框架。
- BepInEx 版檢查點只存在於當次場景／玩家實例；Live 版錨點只存在於工具執行期間。
- F6 不是穿牆導航；未知場景的 Live 緊急候選位置仍需由玩家確認是否安全。

《Heroes & Redemption》及其資產屬於各自權利人。本專案依 [MIT License](LICENSE) 發布，與遊戲開發者及發行商沒有關聯。
