# Main Workspace Page Override (`MainWindow.axaml`)

> **Parent Master:** `design-system/translator/MASTER.md`
> **Target Stack:** .NET 9 + Avalonia UI 12 (`FluentTheme` Dark Mode OLED)
> **Scope:** Main Desktop Studio Workspace (Scanner, Full-Text Search, Live Preview, Extractor & Atomic Patcher)

---

## 1. Three-Layer Token Mapping for Avalonia (`DesignTokens.axaml`)

### Layer 1: Primitive Palette
| Token Key | Hex | Purpose |
|-----------|-----|---------|
| `Color.Slate.950` | `#0B1120` | Deep OLED window & inset background |
| `Color.Slate.900` | `#0F172A` | Secondary dark surface |
| `Color.Slate.850` | `#151F32` | Primary elevated card surface |
| `Color.Slate.800` | `#1E293B` | Control background & hover surface |
| `Color.Slate.750` | `#243247` | Table header & active item surface |
| `Color.Slate.700` | `#334155` | Default subtle border |
| `Color.Slate.600` | `#475569` | Strong / hover border |
| `Color.Slate.50`  | `#F8FAFC` | Primary text (Contrast > 15:1) |
| `Color.Slate.200` | `#E2E8F0` | Secondary readable text (Contrast > 11:1) |
| `Color.Slate.400` | `#94A3B8` | Muted metadata text (Contrast > 5.5:1, passes WCAG AA) |

### Layer 2: Semantic Brushes
| Semantic Brush Key | Target | Notes |
|--------------------|--------|-------|
| `Brush.Window.Background` | `#0B1120` | Root Window background |
| `Brush.Surface.Card` | `#151F32` | Top bar, filter bar, left/right panes, bottom bar |
| `Brush.Surface.Inset` | `#0B1120` | ListBox & Preview Table background |
| `Brush.Surface.Elevated` | `#1E293B` | Secondary buttons, badges, inputs |
| `Brush.Surface.TableHeader` | `#1E293B` | Preview table column header bar |
| `Brush.Accent.Primary` | `#3B82F6` | Folder selection & primary focus |
| `Brush.Accent.Extract` | `#10B981` | Primary CTA: Extract translations |
| `Brush.Accent.Patch` | `#8B5CF6` | Primary CTA: Atomic Game Patch (Folder) |
| `Brush.Accent.PatchSecondary` | `#6366F1` | Secondary CTA: Patch single file |
| `Brush.Status.Warning` | `#D97706` | Restore Original backup action |
| `Brush.Status.DangerBg` | `#451A1A` | Encrypted file warning badge background |
| `Brush.Status.DangerText` | `#FCA5A5` | Encrypted file warning text |

---

## 2. Layout & Density Rules (Density 8/10 — Dense Desktop Studio)

1. **4-Row Workspace Grid (`14px` outer margin, `10px` row gaps):**
   - **Row 0 (Header & Game Profile Bar):** App brand badge with vector icon, Game Path input, Browse / Rescan / Cancel actions, Detected Engine banner, `.translator_backup` SHA-256 Vault status pill, Restore Original CTA, and Encrypted Warning badge.
   - **Row 1 (Search, Language Filter & Source Toggles):** Search `TextBox` with inner left `Icon.Search` & right clear button, preview filter toggle, single unified Source Language `ComboBox`, and 4 Source Kind checkboxes + Smart Noise Filter switch.
   - **Row 2 (Split Master-Detail Workspace):**
     - Left Pane (`460px` default, resizable via visible `GridSplitter` with grip pill): Target list with `LOOSE` / `TEXT` / `MONO` / `CODE` semantic badges, relevance score, and sentence count.
     - Right Pane (`*`): Target title & metadata bar, indeterminate loading bar (`IsPreviewLoading`), and 3 Tabs with vector icons (*Bảng Câu Chữ Đã Bóc Tách*, *Nội Dung Raw*, *Cảnh Báo File Nghi Mã Hóa*).
   - **Row 3 (Status, Progress & Export/Patch Action Bar):** Live status text + smooth `ProgressBar`, Export format checkboxes (`JSON`, `CSV`, `Raw`), and grouped Extract / Atomic Patch CTAs with vector icons.
