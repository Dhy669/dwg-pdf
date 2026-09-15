# DWG 批量按图框输出 PDF

采用一个 `accoreconsole.exe` 进程加载 Managed ObjectARX 插件；插件对每个 DWG 创建旁路 `Database`，不会逐文件打开 AutoCAD 文档。

## 运行

在 PowerShell 中：

```powershell
.\run.ps1
```

自定义目录：

```powershell
.\run.ps1 -InputRoot 'D:\DWG' -OutputRoot 'D:\PDF'
```

输出保持输入子目录结构。纸空间布局通常每个布局输出一份，文件名为 `原DWG名_布局名.pdf`。Model 空间或同一布局检测到多个图框时，按从上到下、从左到右追加 `_01`、`_02`，以免文件互相覆盖。

图框候选包括 4–8 顶点的闭合多段线，以及名称含 `TITLE/BORDER/FRAME/图框/标题栏` 的块；会按尺寸、长宽比和重叠度过滤。阈值可在 `run.ps1` 的 `MinFrameWidth/MinFrameHeight` 中调整。

运行日志位于 `DwgBatchPdf\bin\Release\batch.log`。
