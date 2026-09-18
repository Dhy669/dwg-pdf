using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(DwgBatchPdf.Commands))]

namespace DwgBatchPdf
{
    public sealed class Job
    {
        public string InputRoot { get; set; }
        public string OutputRoot { get; set; }
        public bool IncludeModel { get; set; }
        public bool Recurse { get; set; }
        public bool Overwrite { get; set; }
        public double MinFrameWidth { get; set; }
        public double MinFrameHeight { get; set; }
        public double MinFrameArea { get; set; }
        public double MaxFrameArea { get; set; }
        public string DetectionMode { get; set; }
        public bool DebugFrames { get; set; }
        public string FileNameFilter { get; set; }
        public string ExactFilePath { get; set; }
    }

    internal sealed class Frame
    {
        public Point2d Center;
        public double Width;
        public double Height;
        public double Angle;
        public int SourceDepth;
        public string Source;
        public bool IsValid;
        public string Decision;
        public bool StrongOuterEvidence;
        public string BlockKey;
        public string DrawingNumber;
        public List<Point2d> Outline;
        public bool IsPaperSpace;
        public double ContourArea;
        public double Area { get { return ContourArea > 0 ? ContourArea : Width * Height; } }
    }

    internal sealed class DetectionResult
    {
        public List<Frame> Frames = new List<Frame>();
        public List<Frame> Candidates = new List<Frame>();
    }

    internal sealed class LineSegment
    {
        public Point2d A;
        public Point2d B;
        public int SourceDepth;
    }

    internal sealed class TextMark
    {
        public Point2d Position;
        public string Text;
    }

    public sealed class Commands
    {
        private static readonly HashSet<string> LockedOutputPaths=
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool RepeatedFamilyRejectedAsSmallComponents;
        [CommandMethod("BATCHDWGTOPDF")]
        public void BatchDwgToPdf()
        {
            Trace("command enter");
            Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;
            string baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string jobPath = Path.Combine(baseDir, "batchjob.json");
            string logPath = Path.Combine(baseDir, "batch.log");
            var log = new StringBuilder();
            int ok = 0, failed = 0;
            try
            {
                Trace("before read job");
                Job job = new JavaScriptSerializer().Deserialize<Job>(File.ReadAllText(jobPath, Encoding.UTF8));
                Trace("after read job");
                Validate(job);
                // The target DWG is opened later by ProcessSideDwg. Configure the
                // fallback here, after NETLOAD but before that open. FONTALT is
                // read-only to an AutoCAD 2018 .scr file and putting it in run.scr
                // aborts the script before this command can start.
                try { Application.SetSystemVariable("TEXTFILL", 1); } catch { }
                Directory.CreateDirectory(job.OutputRoot);
                SearchOption option = job.Recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                string filter = string.IsNullOrWhiteSpace(job.FileNameFilter) ? "*.dwg" : job.FileNameFilter;
                string[] files = !string.IsNullOrWhiteSpace(job.ExactFilePath)
                    ? new[] { Path.GetFullPath(job.ExactFilePath) }
                    : Directory.GetFiles(job.InputRoot, filter, option);
                Trace("files=" + files.Length);
                log.AppendLine("Started: " + DateTime.Now.ToString("s"));
                log.AppendLine("DWG count: " + files.Length);
                foreach (string dwg in files)
                {
                    try
                    {
                        int count = ProcessDwg(dwg, job, log);
                        ok += count;
                        log.AppendLine("OK [" + count + "] " + dwg);
                    }
                    catch (System.Exception ex)
                    {
                        failed++;
                        log.AppendLine("ERROR " + dwg + Environment.NewLine + ex);
                    }
                    File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(true));
                }
            }
            catch (System.Exception ex)
            {
                failed++;
                log.AppendLine("FATAL " + ex);
            }
            log.AppendLine(string.Format(CultureInfo.InvariantCulture, "Finished: {0:s}; PDFs={1}; failed DWGs={2}", DateTime.Now, ok, failed));
            File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(true));
            ed.WriteMessage("\nBATCHDWGTOPDF finished. PDFs={0}, failed DWGs={1}. Log: {2}\n", ok, failed, logPath);
        }

        private static void Validate(Job job)
        {
            if (job == null || string.IsNullOrWhiteSpace(job.InputRoot) || !Directory.Exists(job.InputRoot))
                throw new InvalidOperationException("batchjob.json 中的 InputRoot 不存在。");
            if (string.IsNullOrWhiteSpace(job.OutputRoot)) throw new InvalidOperationException("OutputRoot 不能为空。");
            if (job.MinFrameWidth <= 0) job.MinFrameWidth = 100;
            if (job.MinFrameHeight <= 0) job.MinFrameHeight = 100;
            if (job.MinFrameArea < 0) job.MinFrameArea = 0;
            if (job.MaxFrameArea < 0) job.MaxFrameArea = 0;
            if (job.MaxFrameArea > 0 && job.MinFrameArea > job.MaxFrameArea)
                throw new InvalidOperationException("MinFrameArea 不能大于 MaxFrameArea。");
            if (!string.Equals(job.DetectionMode, "BlockOnly", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(job.DetectionMode, "BlockFirst", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(job.DetectionMode, "ContourFirst", StringComparison.OrdinalIgnoreCase))
                job.DetectionMode = "BlockOnly";
        }

        private static int ProcessDwg(string path, Job job, StringBuilder log)
        {
            Trace("ProcessDwg enter " + path);
            CleanupPreviousOutputs(path, job, log);
            Document active = Application.DocumentManager.MdiActiveDocument;
            Trace("active acquired; filename=" + (active == null ? "<null>" : active.Database.Filename));
            if (active != null && string.Equals(Path.GetFileName(active.Database.Filename), Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))
                return ProcessActiveDwg(active.Database, path, job, log);
            Trace("using side database");
            return ProcessSideDwg(path, job, log);
        }

        private static void CleanupPreviousOutputs(string dwg, Job job, StringBuilder log)
        {
            // Remove stale pages before frame detection. Previously cleanup ran
            // only inside the sheet loop, so a run that correctly rejected every
            // old false/slanted frame left zero-byte PDFs from an earlier run in
            // place and made them look like newly generated blank pages.
            string inputRoot = Path.GetFullPath(job.InputRoot).TrimEnd(Path.DirectorySeparatorChar);
            string dwgDirectory = Path.GetDirectoryName(Path.GetFullPath(dwg));
            string relativeDirectory = dwgDirectory.Length > inputRoot.Length
                ? dwgDirectory.Substring(inputRoot.Length).TrimStart(Path.DirectorySeparatorChar)
                : string.Empty;
            string outputDirectory = Path.Combine(job.OutputRoot, relativeDirectory);
            if (!Directory.Exists(outputDirectory)) return;
            string prefix = Safe(Path.GetFileNameWithoutExtension(dwg)) + "_";
            string[] oldPdfs=Directory.GetFiles(outputDirectory, prefix + "*.pdf");
            foreach (string old in oldPdfs)
            {
                try
                {
                    EnsureOutputCanBeReplaced(old);
                    File.Delete(old);
                    log.AppendLine("DELETE OLD PDF " + old);
                }
                catch(System.Exception ex)
                {
                    LockedOutputPaths.Add(Path.GetFullPath(old));
                    log.AppendLine("KEEP LOCKED OLD PDF; new output will use another name: "+old+" / "+ex.Message);
                }
            }
        }

        private static string AvailableOutputPath(string desired)
        {
            string full=Path.GetFullPath(desired);
            if(!File.Exists(full) && !LockedOutputPaths.Contains(full)) return full;
            if(File.Exists(full) && !LockedOutputPaths.Contains(full))
            {
                try { EnsureOutputCanBeReplaced(full); File.Delete(full); return full; }
                catch { LockedOutputPaths.Add(full); }
            }
            string dir=Path.GetDirectoryName(full),name=Path.GetFileNameWithoutExtension(full),ext=Path.GetExtension(full);
            for(int i=1;i<10000;i++)
            {
                string candidate=Path.Combine(dir,name+"_new"+(i==1?string.Empty:"_"+i.ToString("00"))+ext);
                if(!File.Exists(candidate)) return candidate;
            }
            throw new InvalidOperationException("无法为被占用的 PDF 生成唯一的替代文件名："+desired);
        }

        private static void EnsureOutputCanBeReplaced(string path)
        {
            try
            {
                using(var stream=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) { }
            }
            catch(IOException ex)
            {
                throw new InvalidOperationException(
                    "旧 PDF 正被 WPS、浏览器或 PDF 阅读器占用。请关闭后重新运行；本次未删除任何旧 PDF："+path,ex);
            }
            catch(UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException(
                    "旧 PDF 无法覆盖，请检查是否只读或权限不足；本次未删除任何旧 PDF："+path,ex);
            }
        }

        private static int ProcessSideDwg(string path, Job job, StringBuilder log)
        {
            var sheets = new List<KeyValuePair<string, Frame>>();
            using (var db = new Database(false, true))
            {
                Trace("side database before ReadDwgFile");
                db.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null);
                Trace("side database after ReadDwgFile");
                db.CloseInput(true);
                // Never ResolveXrefs on a side database in AutoCAD 2018. Missing,
                // circular or network XREFs can terminate accoreconsole without a
                // managed exception. The real plotting document loads all available
                // references when DocumentCollection.Open is called below.
                log.AppendLine("XREF RESOLVE DEFERRED TO PLOTTING DOCUMENT " + path);
                Database previous = HostApplicationServices.WorkingDatabase;
                HostApplicationServices.WorkingDatabase = db;
                try
                {
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        DBDictionary layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                        var items = new List<KeyValuePair<string, ObjectId>>();
                        foreach (DBDictionaryEntry e in layouts) items.Add(new KeyValuePair<string, ObjectId>(e.Key, e.Value));
                        items.Sort((a, b) => ((Layout)tr.GetObject(a.Value, OpenMode.ForRead)).TabOrder.CompareTo(((Layout)tr.GetObject(b.Value, OpenMode.ForRead)).TabOrder));
                        foreach (var item in items)
                        {
                            Layout layout = (Layout)tr.GetObject(item.Value, OpenMode.ForRead);
                            bool isModel = layout.ModelType;
                            // Every DWG view is an independent source. Model must
                            // not disappear merely because paper layouts exist.
                            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                            if(!isModel && !PaperLayoutHasPrintableContent(btr,tr))
                            {
                                log.AppendLine("SKIP blank paper layout: "+path+" / "+layout.LayoutName);
                                continue;
                            }
                            DetectionResult detection = DetectFrames(btr, tr, job);
                            List<Frame> frames = ConsolidateDetectedFrames(detection.Frames,detection.Candidates);
                            detection.Frames=frames;
                            if (job.DebugFrames)
                            {
                                WriteCandidateCsv(path, layout.LayoutName, detection.Candidates, job);
                                if (isModel) WriteDebugDwg(path, detection.Candidates, job, log);
                            }
                            AppendCandidateSummary(log, path, layout.LayoutName, detection.Candidates);
                            log.AppendLine("FRAMES [" + frames.Count + "] " + path + " / " + layout.LayoutName);
                            for (int fi = 0; fi < frames.Count; fi++)
                            {
                                Frame f = frames[fi];
                                f.IsPaperSpace=!isModel;
                                log.AppendLine(string.Format(CultureInfo.InvariantCulture,
                                    "  FRAME {0}: C=({1:R},{2:R}) W={3:R} H={4:R} A={5:R} DEPTH={6}",
                                    fi + 1, f.Center.X, f.Center.Y, f.Width, f.Height, f.Angle, f.SourceDepth));
                            }
                            // Always plot the detected sheet boundary, even when a
                            // PaperSpace layout has only one frame. Plotting the
                            // whole layout reintroduces sidebars and neighbour
                            // fragments outside that frame.
                            if (frames.Count == 0)
                            {
                                if(!isModel)
                                {
                                    frames=BuildViewportFallbackFrames(btr,tr);
                                    log.AppendLine("FALLBACK paper viewports ["+frames.Count+"]: "+path+" / "+layout.LayoutName);
                                    foreach(Frame frame in frames)
                                    {
                                        frame.IsPaperSpace=true;
                                        sheets.Add(new KeyValuePair<string,Frame>(layout.LayoutName,frame));
                                    }
                                }
                                else log.AppendLine("WARN no frame: " + path + " / " + layout.LayoutName);
                                continue;
                            }
                            foreach (Frame frame in frames) sheets.Add(new KeyValuePair<string, Frame>(layout.LayoutName, frame));
                        }
                        tr.Commit();
                    }
                }
                finally { HostApplicationServices.WorkingDatabase = previous; }
            }

            DocumentCollection docs = Application.DocumentManager;
            Document doc = docs.MdiActiveDocument;
            string activeFile = doc.Database.Filename;
            bool opened = string.IsNullOrWhiteSpace(activeFile) ||
                !string.Equals(Path.GetFullPath(activeFile), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
            if (opened) doc = docs.Open(path, false);
            if (opened) docs.MdiActiveDocument = doc;
            try
            {
                // ProcessSideDwg detects through a side database but plots the
                // real document. Font substitutions must therefore be applied
                // to the plotting database as well.
                PrepareMissingFonts(doc.Database,log);
                for (int i = 0; i < sheets.Count; i++)
                {
                        KeyValuePair<string, Frame> sheet = sheets[i];
                        ObjectId layoutId;
                        using (Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
                        {
                            DBDictionary layouts = (DBDictionary)tr.GetObject(doc.Database.LayoutDictionaryId, OpenMode.ForRead);
                            layoutId = layouts.GetAt(sheet.Key);
                            tr.Commit();
                        }
                        int sameLayoutCount = sheets.Count(s => s.Key == sheet.Key);
                        int sameLayoutIndex = sheets.Take(i + 1).Count(s => s.Key == sheet.Key) - 1;
                        string output = AvailableOutputPath(OutputPath(path, job, sheet.Key, sameLayoutIndex, sameLayoutCount, sheet.Value));
                    if (job.Overwrite || !File.Exists(output)) Plot(doc.Database, layoutId, sheet.Value, output);
                }
            }
            finally { if (opened) doc.CloseAndDiscard(); }
            return sheets.Count;
        }

        private static int ProcessActiveDwg(Database db, string path, Job job, StringBuilder log)
        {
            Trace("ProcessActive enter");
            try
            {
                // Core Console may open the host DWG without loading all nested
                // references. Plotting then contains the border/title information
                // but misses part or all of the referenced drawing geometry.
                db.ResolveXrefs(true, false);
                log.AppendLine("XREFS RESOLVED " + path);
            }
            catch (System.Exception ex)
            {
                log.AppendLine("WARN XREF RESOLVE " + path + " / " + ex.Message);
            }
            PrepareMissingFonts(db, log);
            var sheets = new List<KeyValuePair<string, Frame>>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Trace("active transaction started");
                DBDictionary layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                var orderedLayouts=new List<Layout>();
                foreach(DBDictionaryEntry e in layouts)
                    orderedLayouts.Add((Layout)tr.GetObject(e.Value,OpenMode.ForRead));
                orderedLayouts=orderedLayouts.OrderBy(l=>l.ModelType ? 1 : 0).ThenBy(l=>l.TabOrder).ToList();
                foreach (Layout layout in orderedLayouts)
                {
                    // Process Model plus every non-empty PaperSpace layout.
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                    // A newly-created/unused Layout1 must not suppress real sheets
                    // drawn in ModelSpace.
                    if(!layout.ModelType && !PaperLayoutHasPrintableContent(btr,tr))
                    {
                        log.AppendLine("SKIP blank paper layout: "+path+" / "+layout.LayoutName);
                        continue;
                    }
                    Trace("before DetectFrames");
                    DetectionResult detection = DetectFrames(btr, tr, job);
                    List<Frame> frames = ConsolidateDetectedFrames(detection.Frames,detection.Candidates);
                    detection.Frames=frames;
                    if (job.DebugFrames)
                    {
                        WriteCandidateCsv(path, layout.LayoutName, detection.Candidates, job);
                        WriteDebugDwg(path, detection.Candidates, job, log);
                    }
                    AppendCandidateSummary(log, path, layout.LayoutName, detection.Candidates);
                    Trace("after DetectFrames count=" + frames.Count);
                    log.AppendLine("FRAMES [" + frames.Count + "] " + path + " / " + layout.LayoutName);
                    if(!layout.ModelType)
                    {
                        // A viewport or a few paper-space entities alone do not prove
                        // that the layout is a complete printable sheet.  Only a
                        // successfully detected frame gives PaperSpace priority;
                        // otherwise continue on to ModelSpace instead of emitting a
                        // tiny/blank layout PDF and suppressing the real model sheets.
                        if(frames.Count==0)
                        {
                            // Borders are frequently visible only through one or more
                            // paper viewports.  Plotting the saved Layout settings in
                            // that case produced a 1--3 KiB blank PDF.  Treat every
                            // live viewport as a sheet seed and retain its neighbouring
                            // paper entities (border/title block) instead.
                            frames=BuildViewportFallbackFrames(btr,tr);
                            log.AppendLine("FALLBACK paper viewports ["+frames.Count+"]: "+path+" / "+layout.LayoutName);
                            foreach(Frame viewportFrame in frames)
                            {
                                viewportFrame.IsPaperSpace=true;
                                sheets.Add(new KeyValuePair<string,Frame>(layout.LayoutName,viewportFrame));
                            }
                            continue;
                        }
                    }
                    for (int fi = 0; fi < frames.Count; fi++)
                    {
                        Frame frame = frames[fi];
                        frame.IsPaperSpace=!layout.ModelType;
                        Trace(string.Format(CultureInfo.InvariantCulture,
                            "frame {0}: C=({1:R},{2:R}) W={3:R} H={4:R} A={5:R} D={6}",
                            fi + 1, frame.Center.X, frame.Center.Y, frame.Width, frame.Height, frame.Angle, frame.SourceDepth));
                        log.AppendLine(string.Format(CultureInfo.InvariantCulture,
                            "  FRAME {0}: C=({1:R},{2:R}) W={3:R} H={4:R} A={5:R} DEPTH={6}",
                            fi + 1, frame.Center.X, frame.Center.Y, frame.Width, frame.Height, frame.Angle, frame.SourceDepth));
                        sheets.Add(new KeyValuePair<string, Frame>(layout.LayoutName, frame));
                    }
                }
                tr.Commit();
            }
            var valid = new List<Tuple<string,Frame,string>>();
            var cleanedLayouts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < sheets.Count; i++)
            {
                Trace("before plot " + (i + 1));
                KeyValuePair<string, Frame> sheet = sheets[i];
                ObjectId layoutId;
                using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    DBDictionary layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                    layoutId = layouts.GetAt(sheet.Key);
                    tr.Commit();
                }
                string output = AvailableOutputPath(OutputPath(path, job, sheet.Key, i, sheets.Count, sheet.Value));
                if (!cleanedLayouts.Contains(sheet.Key))
                {
                    string dir = Path.GetDirectoryName(output);
                    string prefix = Safe(Path.GetFileNameWithoutExtension(path)) + "_" + Safe(sheet.Key);
                    foreach (string old in Directory.GetFiles(dir, prefix + "*.pdf"))
                    {
                        if(LockedOutputPaths.Contains(Path.GetFullPath(old))) continue;
                        try { DeleteOutputOrExplain(old); }
                        catch { LockedOutputPaths.Add(Path.GetFullPath(old)); }
                    }
                    cleanedLayouts.Add(sheet.Key);
                }
                try
                {
                    if (job.Overwrite || !File.Exists(output)) Plot(db, layoutId, sheet.Value, output);
                }
                catch (System.Exception ex)
                {
                    if (File.Exists(output)) File.Delete(output);
                    log.AppendLine("SKIP PLOT ERROR " + (i + 1) + " " + path + " / " + ex);
                    continue;
                }
                Trace("after plot " + (i + 1));
                var info = new FileInfo(output);
                long generatedBytes=info.Exists ? info.Length : 0;
                log.AppendLine("PDF BYTES ["+generatedBytes+"] FRAME "+(i+1)+" "+path);
                // File size is not a valid blank-page test. Vector-only sheets,
                // especially PaperSpace frames backed by a viewport, can be well
                // below 8 KiB and were previously deleted even though AutoCAD had
                // successfully produced a page. Only a missing/zero-byte output is
                // unquestionably invalid; visual/content validation belongs in a
                // separate post-processing step and must never silently lose sheets.
                // AutoCAD's truly blank one-page PDF is normally about 1.5 KiB.
                // Do not count that output as a successfully exported sheet.
                if (!info.Exists || info.Length < 2048)
                {
                    if (info.Exists) info.Delete();
                    log.AppendLine("SKIP EMPTY FRAME " + (i + 1) + " " + path);
                }
                else valid.Add(Tuple.Create(sheet.Key,sheet.Value,output));
            }
            // Rename after validation so removed empty candidates do not leave numbering gaps.
            for (int i = 0; i < valid.Count; i++)
            {
                string finalPath = OutputPath(path, job, valid[i].Item1, i, valid.Count, valid[i].Item2);
                if(LockedOutputPaths.Contains(Path.GetFullPath(finalPath))) finalPath=AvailableOutputPath(finalPath);
                if (!string.Equals(valid[i].Item3, finalPath, StringComparison.OrdinalIgnoreCase))
                {
                    MovePlotOutputWithRetry(valid[i].Item3,finalPath,log);
                }
            }
            log.AppendLine("VALID PDFS [" + valid.Count + "] " + path);
            if(valid.Count!=sheets.Count)
            {
                string message="PDF COUNT MISMATCH: detected frames="+sheets.Count+
                    ", generated PDFs="+valid.Count+" / "+path;
                log.AppendLine("ALERT "+message);
                // Preserve successful pages and continue the batch. A legacy
                // layout that rejects Window plotting must not invalidate the
                // complete ModelSpace sheets already generated for this DWG.
                if(valid.Count==0) throw new InvalidOperationException(message);
            }
            return valid.Count;
        }

        private static void MovePlotOutputWithRetry(string source,string destination,StringBuilder log)
        {
            System.Exception last=null;
            for(int attempt=1;attempt<=20;attempt++)
            {
                try
                {
                    if(File.Exists(destination)) DeleteOutputOrExplain(destination);
                    File.Move(source,destination);
                    return;
                }
                catch(IOException ex)
                {
                    last=ex;
                    System.Threading.Thread.Sleep(250);
                }
                catch(UnauthorizedAccessException ex)
                {
                    last=ex;
                    System.Threading.Thread.Sleep(250);
                }
            }
            // A delayed PDF driver handle must not invalidate every successfully
            // plotted page in this DWG. Keep the available temporary name and
            // record the rename failure for a later cleanup pass.
            log.AppendLine("WARN PDF RENAME RETAINED TEMP FILE: "+source+" -> "+destination+
                " / "+(last==null ? "unknown error" : last.Message));
        }

        private static void DeleteOutputOrExplain(string path)
        {
            try { File.Delete(path); }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    "旧 PDF 正被 WPS、浏览器或 PDF 阅读器占用，无法生成新版。请关闭该文件后重新运行：" + path, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException(
                    "旧 PDF 无法覆盖，请检查文件是否只读或被其他程序占用：" + path, ex);
            }
        }

        private static string DebugOutputDirectory(string dwg, Job job)
        {
            string root=Path.GetFullPath(job.InputRoot).TrimEnd(Path.DirectorySeparatorChar);
            string sourceDir=Path.GetDirectoryName(Path.GetFullPath(dwg));
            string relative=sourceDir.Length>root.Length ? sourceDir.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar) : string.Empty;
            string dir=Path.Combine(job.OutputRoot,relative,"_FrameDebug");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void WriteCandidateCsv(string dwg, string layout, List<Frame> candidates, Job job)
        {
            string file=Path.Combine(DebugOutputDirectory(dwg,job),
                Safe(Path.GetFileNameWithoutExtension(dwg))+"_"+Safe(layout)+"_candidates.csv");
            var csv=new StringBuilder();
            csv.AppendLine("Index,Source,Depth,CenterX,CenterY,MinX,MinY,MaxX,MaxY,Width,Height,Area,AngleRadians,Valid,Decision");
            for(int i=0;i<candidates.Count;i++)
            {
                Frame f=candidates[i];
                csv.AppendFormat(CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3:R},{4:R},{5:R},{6:R},{7:R},{8:R},{9:R},{10:R},{11:R},{12:R},{13},{14}\r\n",
                    i+1,Csv(f.Source),f.SourceDepth,f.Center.X,f.Center.Y,
                    f.Center.X-f.Width/2.0,f.Center.Y-f.Height/2.0,
                    f.Center.X+f.Width/2.0,f.Center.Y+f.Height/2.0,
                    f.Width,f.Height,f.Area,f.Angle,f.IsValid?"true":"false",Csv(f.Decision));
            }
            File.WriteAllText(file,csv.ToString(),new UTF8Encoding(true));
        }

        private static void AppendCandidateSummary(StringBuilder log, string dwg, string layout, List<Frame> candidates)
        {
            log.AppendLine("CANDIDATES ["+candidates.Count+"] "+dwg+" / "+layout);
            foreach(var group in candidates.GroupBy(c=>c.Decision??"unclassified").OrderByDescending(g=>g.Count()))
                log.AppendLine("  DECISION "+group.Key+" ["+group.Count()+"]");
            foreach(Frame f in candidates.OrderByDescending(c=>c.Area).Take(12))
                log.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  CANDIDATE {0} C=({1:R},{2:R}) W={3:R} H={4:R} AREA={5:R} SOURCE={6} RESULT={7}",
                    f.IsValid?"VALID":"REJECT",f.Center.X,f.Center.Y,f.Width,f.Height,f.Area,f.Source??"",f.Decision??""));
        }

        private static string Csv(string value)
        {
            value=value??string.Empty;
            return "\""+value.Replace("\"","\"\"")+"\"";
        }

        private static void WriteDebugDwg(string sourcePath, List<Frame> candidates, Job job, StringBuilder log)
        {
            string output=Path.Combine(DebugOutputDirectory(sourcePath,job),
                Safe(Path.GetFileNameWithoutExtension(sourcePath))+"_frames_debug.dwg");
            using(var debugDb=new Database(false,true))
            {
                debugDb.ReadDwgFile(sourcePath,FileOpenMode.OpenForReadAndAllShare,true,null);
                debugDb.CloseInput(true);
                using(Transaction tr=debugDb.TransactionManager.StartTransaction())
                {
                    BlockTable bt=(BlockTable)tr.GetObject(debugDb.BlockTableId,OpenMode.ForRead);
                    BlockTableRecord model=(BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace],OpenMode.ForWrite);
                    // Rejected candidates first, accepted candidates last: a
                    // coincident yellow outline cannot hide a valid red outline.
                    foreach(Frame f in candidates.OrderBy(f=>f.IsValid ? 1 : 0))
                    {
                        EnsureFrameOutline(f);
                        var pl=new Polyline(f.Outline.Count);
                        for(int pi=0;pi<f.Outline.Count;pi++) pl.AddVertexAt(pi,f.Outline[pi],0,0,0);
                        pl.Closed=true;
                        pl.ColorIndex=(short)(f.IsValid ? 1 : 2); // red / yellow
                        model.AppendEntity(pl);
                        tr.AddNewlyCreatedDBObject(pl,true);
                    }
                    tr.Commit();
                }
                if(File.Exists(output)) File.Delete(output);
                debugDb.SaveAs(output,DwgVersion.Current);
            }
            log.AppendLine("DEBUG DWG [red=valid yellow=rejected] " + output);
        }

        private static void Trace(string message)
        {
            try
            {
                string p = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "trace.log");
                File.AppendAllText(p, DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
            catch { }
        }

        private static DetectionResult DetectFrames(BlockTableRecord space, Transaction tr, Job job)
        {
            // The block pass is the sheet locator and therefore always runs
            // first.  Contours are only a fallback when no usable locator block
            // exists; they must never create extra pages inside one located sheet.
            if (!string.Equals(job.DetectionMode,"ContourFirst",StringComparison.OrdinalIgnoreCase))
            {
                DetectionResult blockResult=DetectBlockSheets(space,tr,job);
                if(blockResult.Frames.Count>0) return blockResult;
                // BlockOnly remains the preferred strategy, but "no locator block"
                // must not mean "do not print ModelSpace".  A number of supplied
                // DWGs explode or proxy-wrap their title blocks.  In that case run
                // the strict rectangular contour pass below (double-border + text
                // validation) so model sheets are still discoverable.
                Trace("block detection returned zero frames; strict contour fallback");
            }
            double minW=job.MinFrameWidth, minH=job.MinFrameHeight;
            // A real sheet is identified by two nested rectangular polylines: outer border + inner border.
            // This intentionally rejects title cells and other isolated rectangles.
            var rectangles = new List<Frame>();
            var segments = new List<LineSegment>();
            var textPoints = new List<Point2d>();
            CollectRectangles(space, tr, Matrix3d.Identity, 0, minW, minH, rectangles, segments, textPoints);
            Trace("detect collected rectangles="+rectangles.Count+" segments="+segments.Count+" texts="+textPoints.Count);
            // Large engineering models can contain hundreds of thousands of LINE
            // segments. Four-side reconstruction is combinatorial and previously
            // exhausted/crashed accoreconsole before batch.log could be written.
            // Closed polylines/blocks already present in `rectangles` are cheap and
            // sufficient for the fallback; retain only the largest plausible ones.
            bool oversizedContourPass=segments.Count>60000 || rectangles.Count>10000;
            if(oversizedContourPass)
            {
                // Do not reinterpret arbitrary equipment/detail rectangles as
                // sheets when block recognition has already failed in a massive
                // model. Besides the previous O(n^2) crash, a bounded subset still
                // produced dozens of component-only PDFs. Layout sheets detected
                // earlier remain printable; this ModelSpace pass is safely empty.
                Trace("contour fallback skipped for oversized space; rectangles="+rectangles.Count+" segments="+segments.Count);
                return new DetectionResult { Frames=new List<Frame>(), Candidates=new List<Frame>() };
            }
            // LINE pairing is O(n^2) and can stall on large engineering models.
            // Use it only when closed polylines did not already provide a proper
            // inner/outer frame pair.
            // A drawing may use closed polylines for title/detail boxes while its
            // actual outer sheet borders are four independent LINE entities. Do
            // not let an unrelated inner polyline pair disable LINE-frame search.
            // AddLineRectangles itself accepts only long, WCS-horizontal/vertical
            // segments, so slanted drawing geometry is never used as a border.
            if(!oversizedContourPass) AddLineRectangles(segments, minW, minH, rectangles);
            Trace("detect after four-side reconstruction="+rectangles.Count);
            // Some title frames have continuous/double horizontal borders but
            // their vertical borders are split across nested title-blocks. The
            // strict four-side reconstruction above cannot see those sheets.
            // Reconstruct their OUTER rectangle from matching long horizontal
            // border pairs plus an inner parallel line beside both edges.
            // Diagonal entities are excluded by this routine.
            if(!oversizedContourPass) AddDoubleHorizontalBandRectangles(segments, minW, minH, rectangles);
            Trace("detect after horizontal-band reconstruction="+rectangles.Count);
            // The same geometry is often encountered through an outer block,
            // nested block and reconstructed LINE rectangle. Collapse it first.
            bool blockFirst=string.Equals(job.DetectionMode,"BlockFirst",StringComparison.OrdinalIgnoreCase);
            rectangles = rectangles.Where(r => r.Width >= minW*.8 && r.Height >= minH*.8)
                .OrderBy(r => CandidatePriority(r,blockFirst)).ThenByDescending(r => r.Area).Aggregate(new List<Frame>(), (list, r) =>
                { if (!list.Any(x => FrameOverlap(x, r) > .995)) list.Add(r); return list; });
            rectangles=rectangles.OrderBy(r=>CandidatePriority(r,blockFirst)).ThenByDescending(r=>r.Area).ToList();
            var frames = new List<Frame>();
            foreach (Frame outer in rectangles)
            {
                outer.IsValid=false;
                outer.Decision="candidate";
                if (outer.Width < minW || outer.Height < minH) { outer.Decision="below-minimum-size"; continue; }
                if (job.MinFrameArea > 0 && outer.Area < job.MinFrameArea) { outer.Decision="below-minimum-area"; continue; }
                if (job.MaxFrameArea > 0 && outer.Area > job.MaxFrameArea) { outer.Decision="above-maximum-area"; continue; }
                double ratio = outer.Width / outer.Height;
                if (ratio < 1.05 || ratio > 30.0) { outer.Decision="invalid-aspect-ratio"; continue; }
                // Ordinary nested blocks in the supplied drawings contain many
                // complete-looking legacy borders. They are smaller copied sheet
                // fragments, not host drawing frames. Keep only the largest sheet
                // size family in this DWG; the multi-sheet-wrapper test below still
                // rejects a large rectangle that surrounds several real sheets.
                bool hasInner = outer.StrongOuterEvidence ||
                    rectangles.Any(inner => IsInnerBorder(outer, inner)) ||
                    HasInnerBorderEvidence(outer, segments, minW, minH);
                // A sheet must have a real double-line border. Never emit an
                // isolated rectangle merely because it encloses drawing content;
                // tables, equipment outlines and clipping boundaries do that too.
                // The selected plotting boundary is always this outer rectangle.
                if (!hasInner) { outer.Decision="no-double-border-evidence"; continue; }
                // A border with no title/annotation text is normally an empty
                // construction rectangle. Reject it before invoking the plotter.
                if (textPoints.Count(p => PointInside(outer, p)) < 2) { outer.Decision="insufficient-text"; continue; }
                // Reject a large rectangle that encloses several real sheets. This
                // prevents one PDF page from containing two or more adjacent frames.
                var enclosedCenters = new List<Point2d>();
                foreach (Frame child in rectangles)
                {
                    // Only sizeable child sheets prove that an outer rectangle is
                    // a multi-sheet wrapper. Small nested detail/title rectangles
                    // must not cause the real sheet border to be discarded.
                    if (child.Area <= outer.Area*.12 || child.Area >= outer.Area*.80 || !ContainsFrame(outer, child)) continue;
                    if (!rectangles.Any(inner => IsInnerBorder(child, inner))) continue;
                    if (!enclosedCenters.Any(c => Distance(c, child.Center) < Math.Min(child.Width, child.Height)*.10))
                        enclosedCenters.Add(child.Center);
                }
                if (enclosedCenters.Count >= 2) { outer.Decision="contains-multiple-sheets"; continue; }
                // Once a valid outer sheet has been accepted, never emit one of
                // its title cells/detail boxes as another PDF page.
                if (frames.Any(parent => ContainsFrame(parent, outer))) { outer.Decision="inside-accepted-sheet"; continue; }
                bool duplicate = frames.Any(f => FrameOverlap(f, outer) > .82 ||
                    (Distance(f.Center, outer.Center) < Math.Min(f.Height, outer.Height)*.08 && AngleDifference(f.Angle, outer.Angle) < .02));
                if (!duplicate) { outer.IsValid=true; outer.Decision="valid"; frames.Add(outer); }
                else outer.Decision="duplicate";
            }
            // Recognition priority must not affect the final plotting boundary.
            // If a block/title/detail rectangle was accepted before its enclosing
            // sheet, retain only the enclosing outer sheet. Adjacent sheets do
            // not contain one another and therefore remain separate PDFs.
            var inners=frames.Where(f=>frames.Any(o=>
                !object.ReferenceEquals(o,f) && o.Area>f.Area*1.20 && ContainsFrame(o,f))).ToList();
            foreach(Frame inner in inners)
            {
                inner.IsValid=false;
                inner.Decision="inside-larger-valid-sheet";
                frames.Remove(inner);
            }
            frames.Sort((a, b) =>
            {
                double rowTolerance = Math.Min(a.Height, b.Height) * .35;
                double dy = b.Center.Y - a.Center.Y;
                return Math.Abs(dy) > rowTolerance ? Math.Sign(dy) : a.Center.X.CompareTo(b.Center.X);
            });
            return new DetectionResult { Frames=frames, Candidates=rectangles };
        }

        private static DetectionResult DetectBlockSheets(BlockTableRecord space, Transaction tr, Job job)
        {
            List<Frame> namedFrames=DetectNamedFrameBlocks(space,tr,job);
            List<Frame> repeatedOuterFrames=DetectRepeatedOuterFrameFamily(space,tr,job);
            double repeatedMedianArea=repeatedOuterFrames.Count==0 ? 0 :
                repeatedOuterFrames.OrderBy(f=>f.Area).ElementAt(repeatedOuterFrames.Count/2).Area;
            double namedMedianArea=namedFrames.Count==0 ? 0 :
                namedFrames.OrderBy(f=>f.Area).ElementAt(namedFrames.Count/2).Area;
            bool repeatedClearlyOuter=repeatedOuterFrames.Count>=3 &&
                (namedFrames.Count==0 || repeatedOuterFrames.Count>namedFrames.Count ||
                 repeatedMedianArea>namedMedianArea*1.50);
            if(repeatedClearlyOuter)
            {
                Trace("repeated outer-frame family overrides named-block count: outer="+
                    repeatedOuterFrames.Count+" named="+namedFrames.Count+
                    " outerMedianArea="+repeatedMedianArea+" namedMedianArea="+namedMedianArea);
                foreach(Frame frame in repeatedOuterFrames)
                {
                    frame.IsValid=true;
                    frame.Decision="valid-repeated-host-outer-frame";
                }
                return new DetectionResult { Frames=repeatedOuterFrames,
                    Candidates=namedFrames.Concat(repeatedOuterFrames).ToList() };
            }
            if(namedFrames.Count>0)
            {
                namedFrames=namedFrames.OrderByDescending(f=>f.Area).Aggregate(new List<Frame>(),(list,f)=>
                { if(!list.Any(x=>FrameOverlap(x,f)>.90)) list.Add(f); return list; });
                foreach(Frame f in namedFrames) { f.IsValid=true;f.Decision="named-frame-locator"; }
                var namedLocatorCenters=namedFrames.Select(f=>f.Center).ToList();
                // Even a named title-frame block is first treated as a locator.
                // Search outward for the physical outer border before plotting,
                // so the title/sidebar/text surrounding the drawing are retained.
                CalibrateBlockFramesToOuterBorders(space,tr,namedFrames,job);
                foreach(Frame f in namedFrames)
                {
                    if(f.Decision=="valid-block-sheet-border-not-found")
                    {
                        // A title/sidebar block is only a locator. Its own extent
                        // must never be plotted as a sheet; that creates PDFs
                        // containing only the sidebar.
                        f.IsValid=false;
                        f.Decision="rejected-sidebar-without-complete-outer-border";
                    }
                }
                namedFrames=namedFrames.Where(f=>f.IsValid).ToList();
                // One output boundary must represent exactly one locator. This
                // splits touching sheets and rejects a larger wrapper spanning
                // two or more title-frame blocks.
                foreach(Frame f in namedFrames.ToList())
                {
                    int enclosed=namedLocatorCenters.Count(p=>PointInsideLoose(f,p));
                    if(enclosed!=1)
                    {
                        f.IsValid=false;
                        f.Decision=enclosed>1 ? "rejected-border-contains-multiple-sheets" : "rejected-border-misses-locator";
                        namedFrames.Remove(f);
                    }
                }
                namedFrames=RemoveOverlappingSheetBoundaries(namedFrames);
                // A different-size sheet may use a one-off frame definition or
                // an exploded title block. Supplement named locators with valid
                // title-bearing closed frames that are spatially independent of
                // every already located sheet.
                List<Frame> supplemental=DetectClosedPolylineFrames(space,tr,job);
                foreach(Frame candidate in supplemental.OrderByDescending(f=>f.Area))
                {
                    bool belongsToNamed=namedFrames.Any(n=>
                        PointInsideLoose(n,candidate.Center) ||
                        PointInsideLoose(candidate,n.Center) ||
                        (Distance(n.Center,candidate.Center)<Math.Min(n.Width,n.Height)*.12));
                    if(belongsToNamed) continue;
                    candidate.IsValid=true;
                    candidate.Decision="valid-supplemental-titled-closed-frame";
                    namedFrames.Add(candidate);
                }
                namedFrames=RemoveOverlappingSheetBoundaries(namedFrames);
                namedFrames.Sort((a,b)=>a.Center.X.CompareTo(b.Center.X));
                Trace("named frame blocks valid="+namedFrames.Count);
                return new DetectionResult { Frames=namedFrames,Candidates=namedFrames };
            }
            if(RepeatedFamilyRejectedAsSmallComponents)
            {
                // Do not reinterpret a proven family of repeated equipment cells
                // as sheets through the expensive arbitrary-block fallback.
                Trace("skip arbitrary block fallback after small-component family rejection");
                return new DetectionResult { Frames=new List<Frame>(),Candidates=repeatedOuterFrames };
            }
            // No title/sidebar locator exists: only complete closed rectangular
            // borders may define sheets. Never infer page count from arbitrary
            // repeated equipment/detail blocks.
            List<Frame> unlabelledClosedFrames=DetectClosedPolylineFrames(space,tr,job);
            if(unlabelledClosedFrames.Count>0)
            {
                Trace("no sidebar locator; complete closed frames="+unlabelledClosedFrames.Count);
                return new DetectionResult { Frames=unlabelledClosedFrames,Candidates=unlabelledClosedFrames };
            }
            // Do not turn an arbitrary repeated equipment/detail block into a
            // sheet merely because it contains text. Without a named locator or
            // a complete physical border there is no safe print boundary; this
            // fallback produced component-only, zoomed and chaotic PDFs. Every
            // accepted model page must now be backed by an actual outer frame.
            bool allowUnverifiedArbitraryBlockFallback=false;
            if(!allowUnverifiedArbitraryBlockFallback)
            {
                Trace("no verified outer frame; arbitrary block fallback disabled");
                return new DetectionResult { Frames=new List<Frame>(),Candidates=new List<Frame>() };
            }
            var candidates=new List<Frame>();
            var textPoints=new List<Point2d>();
            CollectBlockCandidates(space,tr,Matrix3d.Identity,0,candidates,textPoints);
            candidates=candidates.OrderByDescending(f=>f.Area).Aggregate(new List<Frame>(),(list,f)=>
            {
                if(!list.Any(x=>FrameOverlap(x,f)>.995)) list.Add(f);
                else { f.IsValid=false; f.Decision="duplicate-block-instance"; }
                return list;
            });
            var eligible=new List<Frame>();
            foreach(Frame f in candidates)
            {
                f.IsValid=false;
                if(f.Width<job.MinFrameWidth || f.Height<job.MinFrameHeight) { f.Decision="below-minimum-size"; continue; }
                if(job.MinFrameArea>0 && f.Area<job.MinFrameArea) { f.Decision="below-minimum-area"; continue; }
                if(job.MaxFrameArea>0 && f.Area>job.MaxFrameArea) { f.Decision="above-maximum-area"; continue; }
                double ratio=Math.Max(f.Width,f.Height)/Math.Min(f.Width,f.Height);
                if(ratio<1.05 || ratio>30.0) { f.Decision="invalid-aspect-ratio"; continue; }
                int texts=textPoints.Count(p=>PointInside(f,p));
                if(texts<5) { f.Decision="block-has-insufficient-text"; continue; }
                f.Decision="block-sheet-candidate";
                eligible.Add(f);
            }

            // A parent block that contains two or more sizeable, disjoint sheet
            // blocks is an assembly/wrapper, not one output page.
            foreach(Frame parent in eligible.ToList())
            {
                var children=eligible.Where(c=>!object.ReferenceEquals(c,parent) &&
                    c.Area>=parent.Area*.08 && c.Area<=parent.Area*.80 && ContainsFrame(parent,c)).ToList();
                var centers=new List<Point2d>();
                foreach(Frame child in children)
                    if(!centers.Any(p=>Distance(p,child.Center)<Math.Min(child.Width,child.Height)*.10)) centers.Add(child.Center);
                if(centers.Count>=2)
                {
                    parent.Decision="block-contains-multiple-sheets";
                    eligible.Remove(parent);
                }
            }

            // Repeated sheets normally use repeated instances of one block
            // definition. Select one block family only; otherwise nested title,
            // equipment and system blocks split each sheet into many PDF pages.
            var frames=new List<Frame>();
            var repeatedFamilies=eligible.Where(f=>!string.IsNullOrWhiteSpace(f.BlockKey))
                .GroupBy(f=>f.BlockKey).Where(g=>g.Count()>=2).ToList();
            List<Frame> sheetFamily=null;
            if(repeatedFamilies.Count>0)
            {
                sheetFamily=repeatedFamilies.OrderBy(g=>
                {
                    Frame sample=g.First();
                    double ratio=Math.Max(sample.Width,sample.Height)/Math.Min(sample.Width,sample.Height);
                    double paperRatioPenalty=Math.Abs(Math.Log(ratio/Math.Sqrt(2.0)));
                    double depthPenalty=sample.SourceDepth*.06;
                    double countPenalty=Math.Abs(g.Count()-2)*.20;
                    return paperRatioPenalty+depthPenalty+countPenalty;
                }).ThenByDescending(g=>g.First().Area).First().ToList();
                Trace("block-only selected family="+sheetFamily[0].BlockKey+" instances="+sheetFamily.Count+
                    " depth="+sheetFamily[0].SourceDepth+" size="+sheetFamily[0].Width+"x"+sheetFamily[0].Height);
                foreach(Frame other in eligible.Where(f=>!sheetFamily.Contains(f)))
                    other.Decision="different-block-family";
            }
            IEnumerable<Frame> outputPool=sheetFamily!=null
                ? (IEnumerable<Frame>)sheetFamily
                : (IEnumerable<Frame>)eligible.OrderByDescending(x=>x.Area);
            foreach(Frame f in outputPool.OrderByDescending(x=>x.Area))
            {
                if(frames.Any(parent=>ContainsFrame(parent,f)))
                {
                    f.Decision="inside-larger-sheet-block";
                    continue;
                }
                if(frames.Any(x=>FrameOverlap(x,f)>.82))
                {
                    f.Decision="overlapping-sheet-block";
                    continue;
                }
                f.IsValid=true;
                f.Decision="valid-block-sheet";
                frames.Add(f);
            }
            // Blocks decide HOW MANY sheets and their approximate locations.
            // The nearby outer border decides the exact plot window. Never use a
            // block's outlying drawing geometry to enlarge the PDF page.
            var blockLocatorCenters=frames.Select(f=>f.Center).ToList();
            CalibrateBlockFramesToOuterBorders(space,tr,frames,job);
            List<Frame> locatorContours=null;
            foreach(Frame locator in frames.Where(x=>x.Decision=="valid-block-sheet-border-not-found").ToList())
            {
                if(locatorContours==null) locatorContours=DetectClosedPolylineFrames(space,tr,job);
                Frame border=locatorContours.Where(c=>PointInsideLoose(c,locator.Center) &&
                        blockLocatorCenters.Count(p=>PointInsideLoose(c,p))==1 &&
                        FrameInsideLocatorCell(c,locator.Center,blockLocatorCenters) &&
                        c.Area>=locator.Area*.45 &&
                        c.Width>=job.MinFrameWidth && c.Height>=job.MinFrameHeight)
                    // A double-line frame yields both inner and outer closed
                    // rectangles. The outermost one is the printable boundary;
                    // choosing the smallest one cuts away part of the border.
                    .OrderByDescending(c=>c.Area).FirstOrDefault();
                if(border==null) continue;
                locator.Center=border.Center;
                locator.Width=border.Width;
                locator.Height=border.Height;
                locator.Angle=border.Angle;
                locator.Outline=border.Outline;
                locator.ContourArea=border.ContourArea;
                locator.Decision="valid-block-sheet-rectangular-pline-border";
                locator.IsValid=true;
            }
            // An ordinary block only establishes sheet count/location.  If no
            // enclosing border can be reconstructed, do not plot its internal
            // extents: that is the source of cropped/fragmented PDFs.
            foreach(Frame f in frames.Where(x=>x.Decision=="valid-block-sheet-border-not-found").ToList())
            {
                f.IsValid=false;
                f.Decision="rejected-locator-without-outer-border";
                frames.Remove(f);
            }
            frames=RemoveOverlappingSheetBoundaries(frames);
            frames.Sort((a,b)=>
            {
                double rowTolerance=Math.Min(a.Height,b.Height)*.35;
                double dy=b.Center.Y-a.Center.Y;
                return Math.Abs(dy)>rowTolerance ? Math.Sign(dy) : a.Center.X.CompareTo(b.Center.X);
            });
            if(frames.Count==0 && eligible.Count==0)
            {
                List<Frame> contourFrames=DetectClosedPolylineFrames(space,tr,job);
                if(contourFrames.Count>0)
                {
                    Trace("no block locator; closed polyline fallback="+contourFrames.Count);
                    return new DetectionResult { Frames=contourFrames,
                        Candidates=candidates.Concat(contourFrames).ToList() };
                }
            }
            Trace("block-only candidates="+candidates.Count+" eligible="+eligible.Count+" valid="+frames.Count);
            return new DetectionResult { Frames=frames,Candidates=candidates };
        }

        private static List<Frame> DetectRepeatedOuterFrameFamily(BlockTableRecord space,Transaction tr,Job job)
        {
            RepeatedFamilyRejectedAsSmallComponents=false;
            DateTime started=DateTime.UtcNow;
            var all=new List<Frame>();
            var texts=new List<Point2d>();
            var segments=new List<LineSegment>();
            // Build candidates from host drawing geometry, not from arbitrary
            // deeply nested repeated detail blocks. CollectRectangles skips XREF
            // definitions and records source depth; LINE reconstruction recovers
            // hand-drawn/split outer frames which are not closed PLINEs.
            CollectRectangles(space,tr,Matrix3d.Identity,0,job.MinFrameWidth,job.MinFrameHeight,
                all,segments,texts);
            Trace("repeated outer: collected rectangles="+all.Count+" segments="+segments.Count);

            // Closed PLINE rectangles are cheap and reliable, so try them first.
            // Never run the quadratic LINE-pair reconstruction over every line in
            // a large engineering model: that was the reason a batch appeared to
            // stop permanently on its third DWG.
            List<Frame> closedFamily=SelectRepeatedOuterFrameFamily(all,texts,job);
            if(closedFamily.Count>0 && RepeatedFamilyHasSheetScale(closedFamily,segments,texts))
            {
                Trace("repeated outer: closed family="+closedFamily.Count+" elapsed="+
                    (DateTime.UtcNow-started).TotalSeconds.ToString("0.0",CultureInfo.InvariantCulture)+"s");
                return closedFamily;
            }

            // Reconstruct only shallow, long, axis-aligned possible border lines.
            // The hard cap keeps both pairing routines bounded. If a drawing has
            // more candidates, named blocks/closed contours remain available and
            // the batch continues instead of hanging.
            var borderSegments=segments.Where(s=>s.SourceDepth<=1).Where(s=>
            {
                double dx=Math.Abs(s.B.X-s.A.X),dy=Math.Abs(s.B.Y-s.A.Y);
                bool horizontal=dx>=job.MinFrameWidth*.8 && dy<=Math.Max(dx,1.0)*.001;
                bool vertical=dy>=job.MinFrameHeight*.03 && dx<=Math.Max(dy,1.0)*.001;
                return horizontal || vertical;
            }).OrderByDescending(s=>Math.Max(Math.Abs(s.B.X-s.A.X),Math.Abs(s.B.Y-s.A.Y)))
              .Take(2500).ToList();
            if(borderSegments.Count<2500 && (DateTime.UtcNow-started).TotalSeconds<20.0)
            {
                AddLineRectangles(borderSegments,job.MinFrameWidth,job.MinFrameHeight,all);
                if((DateTime.UtcNow-started).TotalSeconds<35.0)
                    AddDoubleHorizontalBandRectangles(borderSegments,job.MinFrameWidth,job.MinFrameHeight,all);
            }
            else
                Trace("repeated outer: LINE reconstruction skipped; bounded candidates="+
                    borderSegments.Count+" elapsed="+(DateTime.UtcNow-started).TotalSeconds.ToString("0.0",CultureInfo.InvariantCulture)+"s");

            List<Frame> result=SelectRepeatedOuterFrameFamily(all,texts,job);
            if(result.Count>0 && !RepeatedFamilyHasSheetScale(result,segments,texts))
            {
                RepeatedFamilyRejectedAsSmallComponents=true;
                foreach(Frame frame in result)
                {
                    frame.IsValid=false;
                    frame.Decision="rejected-repeated-small-component-family";
                }
                result=new List<Frame>();
            }
            Trace("repeated outer: final family="+result.Count+" elapsed="+
                (DateTime.UtcNow-started).TotalSeconds.ToString("0.0",CultureInfo.InvariantCulture)+"s");
            return result;
        }

        private static bool RepeatedFamilyHasSheetScale(List<Frame> family,List<LineSegment> segments,List<Point2d> texts)
        {
            if(family==null || family.Count==0) return false;
            // Use robust 1%..99% host extents so one stray construction line
            // cannot distort the comparison. A genuine sheet occupies a useful
            // fraction of the model/layout; repeated equipment cells do not.
            var xs=new List<double>();
            var ys=new List<double>();
            foreach(Point2d p in texts) { xs.Add(p.X);ys.Add(p.Y); }
            if(xs.Count<20)
            {
                foreach(LineSegment s in segments.Where(x=>x.SourceDepth<=1).Take(200000))
                {
                    xs.Add(s.A.X);xs.Add(s.B.X);ys.Add(s.A.Y);ys.Add(s.B.Y);
                }
            }
            if(xs.Count<4) return true;
            xs.Sort();ys.Sort();
            int lo=Math.Max(0,(int)(xs.Count*.01));
            int hi=Math.Min(xs.Count-1,(int)(xs.Count*.99));
            double hostWidth=xs[hi]-xs[lo],hostHeight=ys[hi]-ys[lo];
            double hostArea=Math.Max(1.0,hostWidth*hostHeight);
            double medianArea=family.OrderBy(f=>f.Area).ElementAt(family.Count/2).Area;
            double fraction=medianArea/hostArea;
            bool plausible=fraction>=.0025;
            if(!plausible)
                Trace("repeated outer rejected as small components: count="+family.Count+
                    " medianArea="+medianArea.ToString("R",CultureInfo.InvariantCulture)+
                    " host="+hostWidth.ToString("R",CultureInfo.InvariantCulture)+"x"+
                    hostHeight.ToString("R",CultureInfo.InvariantCulture)+
                    " fraction="+fraction.ToString("R",CultureInfo.InvariantCulture));
            return plausible;
        }

        private static List<Frame> SelectRepeatedOuterFrameFamily(List<Frame> all,List<Point2d> texts,Job job)
        {
            var eligible=all.Where(f=>
            {
                double shortSide=Math.Min(f.Width,f.Height),longSide=Math.Max(f.Width,f.Height);
                return f.SourceDepth<=1 &&
                    f.Width>=job.MinFrameWidth && f.Height>=job.MinFrameHeight &&
                    longSide/Math.Max(1.0,shortSide)>=1.05 && longSide/Math.Max(1.0,shortSide)<=8.0 &&
                    (job.MinFrameArea<=0 || f.Area>=job.MinFrameArea) &&
                    (job.MaxFrameArea<=0 || f.Area<=job.MaxFrameArea) &&
                    texts.Count(p=>PointInside(f,p))>=2;
            }).ToList();
            if(eligible.Count<2) return new List<Frame>();

            // Group hand-drawn frames by dimensions with a 5% tolerance. Score
            // by both repeat count and physical area so repeated title cells do
            // not beat six full A0/A1 borders.
            var groups=new List<List<Frame>>();
            foreach(Frame frame in eligible.OrderByDescending(f=>f.Area))
            {
                List<Frame> group=groups.FirstOrDefault(g=>
                {
                    Frame s=g[0];
                    return Math.Abs(s.Width-frame.Width)/Math.Max(s.Width,frame.Width)<.05 &&
                           Math.Abs(s.Height-frame.Height)/Math.Max(s.Height,frame.Height)<.05 &&
                           AngleDifference(s.Angle,frame.Angle)<.04;
                });
                if(group==null) { group=new List<Frame>();groups.Add(group); }
                group.Add(frame);
            }
            // A DWG can contain A0/A1 sheets together, portrait and landscape
            // sheets, or a right-hand group drawn with a slightly different
            // frame size. Selecting only one family silently discarded all the
            // other groups. Keep every repeated family whose physical area is
            // sheet-scale relative to the largest repeated family. Requiring at
            // least two instances still excludes one-off internal detail boxes;
            // the host-area guard then rejects repeated equipment cells.
            var repeatedGroups=groups.Where(g=>g.Count>=2).ToList();
            if(repeatedGroups.Count==0) return new List<Frame>();
            double largestFamilyArea=repeatedGroups.Max(g=>g[0].Area);
            var selectedGroups=repeatedGroups.Where(g=>g[0].Area>=largestFamilyArea*.12)
                .OrderByDescending(g=>g[0].Area).ToList();
            Trace("repeated outer groups="+string.Join(",",selectedGroups.Select(g=>
                g.Count+"x"+g[0].Width.ToString("0.##",CultureInfo.InvariantCulture)+"x"+
                g[0].Height.ToString("0.##",CultureInfo.InvariantCulture))));
            var result=new List<Frame>();
            foreach(Frame frame in selectedGroups.SelectMany(g=>g).OrderByDescending(f=>f.Area))
            {
                if(result.Any(r=>FrameIntersectionOverSmaller(r,frame)>.80)) continue;
                frame.IsValid=true;
                frame.Decision="valid-repeated-outer-frame-family";
                frame.Source="RepeatedHostOuterFrame";
                result.Add(frame);
            }
            result.Sort((a,b)=>
            {
                double rowTolerance=Math.Min(a.Height,b.Height)*.35;
                double dy=b.Center.Y-a.Center.Y;
                return Math.Abs(dy)>rowTolerance ? Math.Sign(dy) : a.Center.X.CompareTo(b.Center.X);
            });
            return result;
        }

        private static List<Frame> DetectClosedPolylineFrames(BlockTableRecord space,Transaction tr,Job job)
        {
            var all=new List<Frame>();
            var texts=new List<Point2d>();
            var textMarks=new List<TextMark>();
            CollectClosedPolylineFrames(space,tr,Matrix3d.Identity,0,all,texts);
            CollectTextMarks(space,tr,Matrix3d.Identity,0,textMarks);
            foreach(Frame f in all)
            {
                f.IsValid=false;
                if(f.Width<job.MinFrameWidth || f.Height<job.MinFrameHeight) { f.Decision="below-minimum-size";continue; }
                if(job.MinFrameArea>0 && f.Area<job.MinFrameArea) { f.Decision="below-minimum-area";continue; }
                if(job.MaxFrameArea>0 && f.Area>job.MaxFrameArea) { f.Decision="above-maximum-area";continue; }
                if(texts.Count(p=>PointInside(f,p))<2) { f.Decision="insufficient-text";continue; }
                if(!HasTitleSignature(f,textMarks)) { f.Decision="no-title-signature-in-lower-right";continue; }
                f.IsValid=true;f.Decision="valid-closed-polyline";
            }
            var valid=all.Where(f=>f.IsValid).OrderByDescending(f=>f.Area).ToList();
            // A large closure surrounding two or more complete peer frames is a
            // sheet-group wrapper, not a sheet. This is common when several
            // adjacent drawings share/meet border lines.
            foreach(Frame wrapper in valid.ToList())
            {
                // Internal title tables and diagram panels often contain many
                // rectangles. They must not make the real outer sheet look like
                // a multi-sheet wrapper. Only large peer frames which together
                // occupy most of the wrapper prove that it surrounds sheets.
                var peerChildren=valid.Where(c=>!object.ReferenceEquals(c,wrapper) &&
                        c.Area>=wrapper.Area*.25 && c.Area<=wrapper.Area*.65 &&
                        ContainsFrame(wrapper,c)).ToList();
                var childCenters=peerChildren.Select(c=>c.Center).ToList();
                var distinct=new List<Point2d>();
                foreach(Point2d p in childCenters)
                    if(!distinct.Any(q=>Distance(p,q)<Math.Min(wrapper.Width,wrapper.Height)*.05)) distinct.Add(p);
                double coveredArea=peerChildren.Sum(c=>c.Area);
                if(distinct.Count>=2 && coveredArea>=wrapper.Area*.65)
                {
                    wrapper.IsValid=false;
                    wrapper.Decision="rejected-closed-wrapper-contains-multiple-sheets";
                    valid.Remove(wrapper);
                }
            }
            var result=new List<Frame>();
            foreach(Frame f in valid)
            {
                if(result.Any(o=>ContainsFrame(o,f) || FrameOverlap(o,f)>.82))
                { f.IsValid=false;f.Decision="merged-overlap";continue; }
                result.Add(f);
            }
            result.Sort((a,b)=>a.Center.X.CompareTo(b.Center.X));
            return result;
        }

        private static void CollectClosedPolylineFrames(BlockTableRecord space,Transaction tr,Matrix3d transform,
            int depth,List<Frame> frames,List<Point2d> texts)
        {
            if(depth>4) return;
            foreach(ObjectId id in space)
            {
                Entity ent=tr.GetObject(id,OpenMode.ForRead,false) as Entity;
                if(ent==null || !ent.Visible || !EntityLayerVisible(ent,tr)) continue;
                DBText dt=ent as DBText;
                if(dt!=null) { Point3d p=dt.Position.TransformBy(transform);texts.Add(new Point2d(p.X,p.Y)); }
                MText mt=ent as MText;
                if(mt!=null) { Point3d p=mt.Location.TransformBy(transform);texts.Add(new Point2d(p.X,p.Y)); }
                Polyline pl=ent as Polyline;
                Frame f;
                if(pl!=null && TryGetClosedPolylineFrame(pl,transform,out f))
                { f.Source="ClosedPolyline";f.SourceDepth=depth;frames.Add(f); }
                BlockReference br=ent as BlockReference;
                if(br==null) continue;
                try
                {
                    BlockTableRecord nested=(BlockTableRecord)tr.GetObject(br.BlockTableRecord,OpenMode.ForRead);
                    if(!nested.IsLayout && !nested.IsFromExternalReference)
                        CollectClosedPolylineFrames(nested,tr,transform*br.BlockTransform,depth+1,frames,texts);
                }
                catch { }
            }
        }

        private static void CollectTextMarks(BlockTableRecord space,Transaction tr,Matrix3d transform,
            int depth,List<TextMark> marks)
        {
            if(depth>6) return;
            foreach(ObjectId id in space)
            {
                Entity ent=tr.GetObject(id,OpenMode.ForRead,false) as Entity;
                if(ent==null || !ent.Visible || !EntityLayerVisible(ent,tr)) continue;
                DBText dt=ent as DBText;
                if(dt!=null)
                {
                    Point3d p=dt.Position.TransformBy(transform);
                    marks.Add(new TextMark { Position=new Point2d(p.X,p.Y),Text=dt.TextString??string.Empty });
                }
                MText mt=ent as MText;
                if(mt!=null)
                {
                    Point3d p=mt.Location.TransformBy(transform);
                    marks.Add(new TextMark { Position=new Point2d(p.X,p.Y),Text=mt.Contents??string.Empty });
                }
                BlockReference br=ent as BlockReference;
                if(br==null) continue;
                try
                {
                    foreach(ObjectId aid in br.AttributeCollection)
                    {
                        AttributeReference ar=tr.GetObject(aid,OpenMode.ForRead,false) as AttributeReference;
                        if(ar==null || ar.Invisible) continue;
                        Point3d p=ar.Position.TransformBy(transform);
                        marks.Add(new TextMark { Position=new Point2d(p.X,p.Y),
                            Text=(ar.Tag+" "+ar.TextString)??string.Empty });
                    }
                    BlockTableRecord nested=(BlockTableRecord)tr.GetObject(br.BlockTableRecord,OpenMode.ForRead);
                    if(!nested.IsLayout && !nested.IsFromExternalReference)
                        CollectTextMarks(nested,tr,transform*br.BlockTransform,depth+1,marks);
                }
                catch { }
            }
        }

        private static bool HasTitleSignature(Frame frame,List<TextMark> marks)
        {
            double c=Math.Cos(-frame.Angle),s=Math.Sin(-frame.Angle);
            foreach(TextMark mark in marks)
            {
                double dx=mark.Position.X-frame.Center.X,dy=mark.Position.Y-frame.Center.Y;
                double x=c*dx-s*dy,y=s*dx+c*dy;
                // Rightmost 42% and lower 55%: includes a conventional right
                // sidebar and the usual lower-right title block.
                if(x<frame.Width*.08 || x>frame.Width*.52 ||
                   y< -frame.Height*.52 || y>frame.Height*.08) continue;
                string value=(mark.Text??string.Empty).ToUpperInvariant();
                if(Regex.IsMatch(value,@"DRAWING\s*(NO|NUMBER|TITLE|NAME)?|DWG\s*(NO|NUMBER)?|SHEET\s*(NO|NUMBER)?|CHECK(ED)?|APPROV(ED|AL)?|AUDIT|TITLE") ||
                   value.Contains("图号") || value.Contains("图名") || value.Contains("审核") ||
                   value.Contains("校对") || value.Contains("批准")) return true;
            }
            return false;
        }

        private static bool TryGetClosedPolylineFrame(Polyline pl,Matrix3d transform,out Frame frame)
        {
            frame=null;
            if(!pl.Closed || pl.NumberOfVertices<4 || pl.NumberOfVertices>200) return false;
            var points=new List<Point2d>();
            for(int i=0;i<pl.NumberOfVertices;i++)
            {
                Point3d p=pl.GetPoint3dAt(i).TransformBy(transform);
                points.Add(new Point2d(p.X,p.Y));
            }
            double twiceArea=0;
            for(int i=0;i<points.Count;i++) twiceArea+=points[i].X*points[(i+1)%points.Count].Y-points[(i+1)%points.Count].X*points[i].Y;
            if(Math.Abs(twiceArea)<1e-6) return false;
            int longest=0;double longestLength=0;
            for(int i=0;i<points.Count;i++)
            { double length=Distance(points[i],points[(i+1)%points.Count]);if(length>longestLength){longest=i;longestLength=length;} }
            Point2d a=points[longest],b=points[(longest+1)%points.Count];
            double angle=Math.Atan2(b.Y-a.Y,b.X-a.X);
            // A rectangle edge has the same orientation in either direction.
            // Normalize equivalent 132-degree and -48-degree results to one
            // stable range so adjacent sheets cannot be printed upside-down.
            while(angle>=Math.PI/2.0) angle-=Math.PI;
            while(angle< -Math.PI/2.0) angle+=Math.PI;
            double c=Math.Cos(-angle),s=Math.Sin(-angle);
            var projected=points.Select(p=>new Point2d(c*p.X-s*p.Y,s*p.X+c*p.Y)).ToList();
            double minX=projected.Min(p=>p.X),maxX=projected.Max(p=>p.X),minY=projected.Min(p=>p.Y),maxY=projected.Max(p=>p.Y);
            double boxArea=(maxX-minX)*(maxY-minY);
            if(boxArea<=1e-6 || Math.Abs(twiceArea)*.5/boxArea<.90) return false;
            // Every material edge must be parallel to one of the two principal
            // rectangle axes. This rejects site boundaries, diagonal drawing
            // geometry and clipping polygons while still allowing a genuinely
            // rotated rectangular sheet and small hand-drawn errors.
            double alignedLength=0,totalLength=0,angleTolerance=Math.PI/90.0;
            for(int i=0;i<points.Count;i++)
            {
                Point2d p1=points[i],p2=points[(i+1)%points.Count];
                double length=Distance(p1,p2);
                if(length<=Math.Max(longestLength*.0001,1e-6)) continue;
                totalLength+=length;
                double edgeAngle=Math.Atan2(p2.Y-p1.Y,p2.X-p1.X);
                double d0=AngleDifference(edgeAngle,angle);
                double d90=AngleDifference(edgeAngle,angle+Math.PI/2.0);
                if(Math.Min(d0,d90)<=angleTolerance) alignedLength+=length;
            }
            if(totalLength<=0 || alignedLength/totalLength<.97) return false;
            double localX=(minX+maxX)/2.0,localY=(minY+maxY)/2.0,ic=Math.Cos(angle),isn=Math.Sin(angle);
            frame=new Frame { Center=new Point2d(ic*localX-isn*localY,isn*localX+ic*localY),
                Width=maxX-minX,Height=maxY-minY,Angle=angle,Outline=points,
                ContourArea=Math.Abs(twiceArea)*.5 };
            return frame.Width>1e-6 && frame.Height>1e-6;
        }

        private static List<Frame> DetectNamedFrameBlocks(BlockTableRecord space,Transaction tr,Job job)
        {
            var result=new List<Frame>();
            CollectNamedFrameBlocks(space,tr,Matrix3d.Identity,0,job,result);
            if(result.Count==0) return result;
            // Sheet count must not be inferred from one repeated block family.
            // A DWG can contain two A0 sheets plus one A1/A2 sheet whose frame
            // definition occurs only once. Keep every frame block at the
            // shallowest insertion level; deeper occurrences are nested title
            // cells belonging to those outer instances.
            int shallow=result.Min(f=>f.SourceDepth);
            return result.Where(f=>f.SourceDepth==shallow)
                .OrderByDescending(f=>f.Area)
                .Aggregate(new List<Frame>(),(list,f)=>
                {
                    bool duplicate=list.Any(x=>
                        Distance(x.Center,f.Center)<Math.Min(Math.Min(x.Width,x.Height),Math.Min(f.Width,f.Height))*.03 &&
                        Math.Abs(x.Width-f.Width)/Math.Max(x.Width,f.Width)<.08 &&
                        Math.Abs(x.Height-f.Height)/Math.Max(x.Height,f.Height)<.08);
                    if(!duplicate) list.Add(f);
                    return list;
                });
        }

        private static void CollectNamedFrameBlocks(BlockTableRecord space,Transaction tr,Matrix3d parentTransform,
            int depth,Job job,List<Frame> result)
        {
            if(depth>4) return;
            foreach(ObjectId id in space)
            {
                BlockReference br=tr.GetObject(id,OpenMode.ForRead,false) as BlockReference;
                if(br==null || !br.Visible || !EntityLayerVisible(br,tr)) continue;
                string name=GetBlockName(br,tr);
                Matrix3d worldTransform=parentTransform*br.BlockTransform;
                try
                {
                    BlockTableRecord definition=(BlockTableRecord)tr.GetObject(br.BlockTableRecord,OpenMode.ForRead);
                    if(LooksLikeFrameName(name))
                    {
                        Frame frame;
                        bool exact=TryGetRobustFrameFromBlock(definition,tr,worldTransform,job,depth,out frame);
                        if(!exact) TryGetTrimmedFrameFromBlock(definition,tr,worldTransform,job,depth,out frame);
                        // Finding a named frame block and reconstructing its border
                        // are separate decisions. Never discard RCJM1-TK-A0 merely
                        // because imperfect/nested edges failed strict closure.
                        if(frame!=null)
                        {
                            frame.Source=(exact ? "NamedFrame:" : "NamedFrameFallback:")+name;
                            frame.BlockKey=(br.DynamicBlockTableRecord.IsNull ? br.BlockTableRecord : br.DynamicBlockTableRecord).Handle.ToString();
                            frame.DrawingNumber=ReadDrawingNumber(br,tr);
                            EnsureFrameOutline(frame);
                            result.Add(frame);
                            Trace("named frame located name="+name+" exact="+exact+" size="+frame.Width+"x"+frame.Height);
                        }
                    }
                    if(!definition.IsLayout && !definition.IsFromExternalReference)
                        CollectNamedFrameBlocks(definition,tr,worldTransform,depth+1,job,result);
                }
                catch { }
            }
        }

        private static bool TryGetRobustFrameFromBlock(BlockTableRecord definition,Transaction tr,Matrix3d transform,
            Job job,int sourceDepth,out Frame frame)
        {
            frame=null;
            var segments=new List<LineSegment>();
            CollectBorderSegmentsBounded(definition,tr,transform,0,2,segments);
            var hs=segments.Where(s=>
            {
                double dx=Math.Abs(s.B.X-s.A.X),dy=Math.Abs(s.B.Y-s.A.Y);
                return dx>=job.MinFrameWidth && dy<=Math.Max(dx,1.0)*.015;
            }).OrderByDescending(s=>Math.Abs(s.B.X-s.A.X)).Take(300).ToList();
            var vs=segments.Where(s=>
            {
                double dx=Math.Abs(s.B.X-s.A.X),dy=Math.Abs(s.B.Y-s.A.Y);
                return dy>=job.MinFrameHeight*.20 && dx<=Math.Max(dy,1.0)*.015;
            }).ToList();
            double bestArea=0;
            for(int i=0;i<hs.Count;i++)
            {
                double ax1=Math.Min(hs[i].A.X,hs[i].B.X),ax2=Math.Max(hs[i].A.X,hs[i].B.X),ay=(hs[i].A.Y+hs[i].B.Y)/2.0;
                for(int j=i+1;j<hs.Count;j++)
                {
                    double bx1=Math.Min(hs[j].A.X,hs[j].B.X),bx2=Math.Max(hs[j].A.X,hs[j].B.X),by=(hs[j].A.Y+hs[j].B.Y)/2.0;
                    double overlap=Math.Max(0,Math.Min(ax2,bx2)-Math.Max(ax1,bx1));
                    double shorter=Math.Min(ax2-ax1,bx2-bx1);
                    if(shorter<=0 || overlap/shorter<.60) continue;
                    double left=Math.Min(ax1,bx1),right=Math.Max(ax2,bx2),bottom=Math.Min(ay,by),top=Math.Max(ay,by);
                    double width=right-left,height=top-bottom;
                    if(width<job.MinFrameWidth || height<job.MinFrameHeight) continue;
                    double ratio=Math.Max(width,height)/Math.Min(width,height);
                    if(ratio<1.05 || ratio>8.0) continue;
                    double tol=Math.Max(width*.04,job.MinFrameWidth*.05);
                    double lc=VerticalCoverageRatio(vs,left,bottom,top,tol),rc=VerticalCoverageRatio(vs,right,bottom,top,tol);
                    if(lc<.20 || rc<.20 || lc+rc<.55) continue;
                    double area=width*height;
                    if(job.MinFrameArea>0 && area<job.MinFrameArea) continue;
                    if(job.MaxFrameArea>0 && area>job.MaxFrameArea) continue;
                    if(area>bestArea)
                    {
                        bestArea=area;
                        frame=new Frame { Center=new Point2d((left+right)/2.0,(bottom+top)/2.0),
                            Width=width*1.015,Height=height*1.015,Angle=0,SourceDepth=sourceDepth };
                    }
                }
            }
            return frame!=null;
        }

        private static bool TryGetTrimmedFrameFromBlock(BlockTableRecord definition,Transaction tr,Matrix3d transform,
            Job job,int sourceDepth,out Frame frame)
        {
            frame=null;
            var segments=new List<LineSegment>();
            CollectBorderSegmentsBounded(definition,tr,transform,0,4,segments);
            var hs=segments.Where(s=>
            {
                double dx=Math.Abs(s.B.X-s.A.X),dy=Math.Abs(s.B.Y-s.A.Y);
                return dx>=job.MinFrameWidth && dy<=Math.Max(dx,1.0)*.02;
            }).OrderByDescending(s=>Math.Abs(s.B.X-s.A.X)).Take(500).ToList();

            // Named title-frame blocks are trusted locators. If vertical sides
            // are nested, broken or absent, two broadly overlapping long
            // horizontal edges are sufficient; choose the largest plausible pair.
            double bestArea=0;
            for(int i=0;i<hs.Count;i++)
            {
                double ax1=Math.Min(hs[i].A.X,hs[i].B.X),ax2=Math.Max(hs[i].A.X,hs[i].B.X),ay=(hs[i].A.Y+hs[i].B.Y)/2.0;
                for(int j=i+1;j<hs.Count;j++)
                {
                    double bx1=Math.Min(hs[j].A.X,hs[j].B.X),bx2=Math.Max(hs[j].A.X,hs[j].B.X),by=(hs[j].A.Y+hs[j].B.Y)/2.0;
                    double shorter=Math.Min(ax2-ax1,bx2-bx1);
                    double overlap=Math.Max(0,Math.Min(ax2,bx2)-Math.Max(ax1,bx1));
                    if(shorter<=0 || overlap/shorter<.55) continue;
                    double left=Math.Min(ax1,bx1),right=Math.Max(ax2,bx2),bottom=Math.Min(ay,by),top=Math.Max(ay,by);
                    double width=right-left,height=top-bottom;
                    if(width<job.MinFrameWidth || height<job.MinFrameHeight) continue;
                    double ratio=Math.Max(width,height)/Math.Min(width,height);
                    if(ratio<1.05 || ratio>8.0) continue;
                    double area=width*height;
                    if(area>bestArea)
                    {
                        bestArea=area;
                        frame=new Frame { Center=new Point2d((left+right)/2.0,(bottom+top)/2.0),
                            Width=width*1.015,Height=height*1.015,Angle=0,SourceDepth=sourceDepth };
                    }
                }
            }
            if(frame!=null) return true;

            // Final robust fallback: trim the outer 2% of endpoint coordinates so
            // one remote entity cannot turn an A0 frame into a 28-million-unit
            // extent. This fallback remains tied to the named frame block and
            // never switches to an internal drawing block family.
            var xs=segments.SelectMany(s=>new[] { s.A.X,s.B.X }).OrderBy(x=>x).ToList();
            var ys=segments.SelectMany(s=>new[] { s.A.Y,s.B.Y }).OrderBy(y=>y).ToList();
            if(xs.Count<8 || ys.Count<8) return false;
            int lo=(int)Math.Floor((xs.Count-1)*.02),hi=(int)Math.Ceiling((xs.Count-1)*.98);
            double minX=xs[lo],maxX=xs[hi],minY=ys[lo],maxY=ys[hi];
            double robustWidth=maxX-minX,robustHeight=maxY-minY;
            if(robustWidth<job.MinFrameWidth || robustHeight<job.MinFrameHeight) return false;
            double robustRatio=Math.Max(robustWidth,robustHeight)/Math.Min(robustWidth,robustHeight);
            if(robustRatio>8.0)
            {
                // Preserve the credible shorter span and restore an A-series
                // proportion around the robust median instead of using the
                // remote outlier dimension.
                double credible=Math.Min(robustWidth,robustHeight);
                double cx=xs[xs.Count/2],cy=ys[ys.Count/2];
                robustWidth=credible;
                robustHeight=credible/Math.Sqrt(2.0);
                minX=cx-robustWidth/2.0;maxX=cx+robustWidth/2.0;
                minY=cy-robustHeight/2.0;maxY=cy+robustHeight/2.0;
            }
            frame=new Frame { Center=new Point2d((minX+maxX)/2.0,(minY+maxY)/2.0),
                Width=(maxX-minX)*1.02,Height=(maxY-minY)*1.02,Angle=0,SourceDepth=sourceDepth };
            return true;
        }

        private static void CollectBorderSegmentsBounded(BlockTableRecord space,Transaction tr,Matrix3d transform,
            int depth,int maxDepth,List<LineSegment> segments)
        {
            if(depth>maxDepth) return;
            foreach(ObjectId id in space)
            {
                Entity ent=tr.GetObject(id,OpenMode.ForRead,false) as Entity;
                if(ent==null || !ent.Visible || !EntityLayerVisible(ent,tr)) continue;
                Line line=ent as Line;
                if(line!=null)
                {
                    Point3d a=line.StartPoint.TransformBy(transform),b=line.EndPoint.TransformBy(transform);
                    segments.Add(new LineSegment { A=new Point2d(a.X,a.Y),B=new Point2d(b.X,b.Y),SourceDepth=depth });
                }
                Polyline pl=ent as Polyline;
                if(pl!=null && pl.NumberOfVertices>=2)
                {
                    int count=pl.Closed ? pl.NumberOfVertices : pl.NumberOfVertices-1;
                    for(int i=0;i<count;i++)
                    {
                        if(Math.Abs(pl.GetBulgeAt(i))>1e-8) continue;
                        Point3d a=pl.GetPoint3dAt(i).TransformBy(transform);
                        Point3d b=pl.GetPoint3dAt((i+1)%pl.NumberOfVertices).TransformBy(transform);
                        segments.Add(new LineSegment { A=new Point2d(a.X,a.Y),B=new Point2d(b.X,b.Y),SourceDepth=depth });
                    }
                }
                BlockReference br=ent as BlockReference;
                if(br==null || depth>=maxDepth) continue;
                try
                {
                    BlockTableRecord nested=(BlockTableRecord)tr.GetObject(br.BlockTableRecord,OpenMode.ForRead);
                    if(!nested.IsLayout) CollectBorderSegmentsBounded(nested,tr,transform*br.BlockTransform,depth+1,maxDepth,segments);
                }
                catch { }
            }
        }

        private static void ExpandFramesWithCompanionBlocks(List<Frame> frames, List<Frame> candidates)
        {
            if(frames.Count==0) return;
            var original=frames.Select(f=>new Frame {
                Center=f.Center,Width=f.Width,Height=f.Height,Angle=f.Angle,
                SourceDepth=f.SourceDepth,Source=f.Source,BlockKey=f.BlockKey
            }).ToList();
            for(int i=0;i<frames.Count;i++)
            {
                Frame anchor=original[i];
                double left=anchor.Center.X-anchor.Width/2.0,right=anchor.Center.X+anchor.Width/2.0;
                double bottom=anchor.Center.Y-anchor.Height/2.0,top=anchor.Center.Y+anchor.Height/2.0;
                double searchHalfW=anchor.Width*.68,searchHalfH=anchor.Height*.68;
                foreach(Frame part in candidates)
                {
                    if(object.ReferenceEquals(part,frames[i]) || part.Area<=0 || part.Area>anchor.Area*.40) continue;
                    double dx=Math.Abs(part.Center.X-anchor.Center.X),dy=Math.Abs(part.Center.Y-anchor.Center.Y);
                    if(dx>searchHalfW || dy>searchHalfH) continue;
                    // Assign each companion to exactly one drawing. This keeps a
                    // shared/nearby title block from enlarging two PDF windows.
                    int nearest=0;
                    double nearestDistance=double.MaxValue;
                    for(int j=0;j<original.Count;j++)
                    {
                        double d=Distance(part.Center,original[j].Center);
                        if(d<nearestDistance) { nearestDistance=d; nearest=j; }
                    }
                    if(nearest!=i) continue;
                    left=Math.Min(left,part.Center.X-part.Width/2.0);
                    right=Math.Max(right,part.Center.X+part.Width/2.0);
                    bottom=Math.Min(bottom,part.Center.Y-part.Height/2.0);
                    top=Math.Max(top,part.Center.Y+part.Height/2.0);
                    part.Decision="companion-of-sheet-"+(i+1);
                }
                // Keep a small safety margin for lineweight and text overhang,
                // while preventing remote/outlier blocks from making the drawing
                // tiny on the PDF page.
                double width=Math.Min((right-left)*1.02,anchor.Width*1.25);
                double height=Math.Min((top-bottom)*1.02,anchor.Height*1.25);
                double centerX=(left+right)/2.0,centerY=(bottom+top)/2.0;
                double anchorLeft=anchor.Center.X-anchor.Width/2.0,anchorRight=anchor.Center.X+anchor.Width/2.0;
                double anchorBottom=anchor.Center.Y-anchor.Height/2.0,anchorTop=anchor.Center.Y+anchor.Height/2.0;
                centerX=Math.Max(anchorRight-width/2.0,Math.Min(anchorLeft+width/2.0,centerX));
                centerY=Math.Max(anchorTop-height/2.0,Math.Min(anchorBottom+height/2.0,centerY));
                if(width>=anchor.Width && height>=anchor.Height)
                {
                    frames[i].Center=new Point2d(centerX,centerY);
                    frames[i].Width=width;
                    frames[i].Height=height;
                }
            }
        }

        private static void CalibrateBlockFramesToOuterBorders(BlockTableRecord space, Transaction tr,
            List<Frame> frames, Job job)
        {
            if(frames.Count==0) return;
            // Preserve locator positions before any frame is calibrated. These
            // points define individual sheets even when adjacent outer borders
            // touch or share a line.
            var locatorCenters=frames.Select(f=>f.Center).ToList();
            var rectangles=new List<Frame>();
            var segments=new List<LineSegment>();
            var textPoints=new List<Point2d>();
            CollectRectangles(space,tr,Matrix3d.Identity,0,job.MinFrameWidth,job.MinFrameHeight,
                rectangles,segments,textPoints);
            // The general detector deliberately skips XREF definitions and only
            // records standalone LINE entities. Border calibration must also see
            // straight edges of LWPOLYLINE/old POLYLINE/3D POLYLINE and resolved
            // XREF content, otherwise a visible frame is absent from the search.
            segments.Clear();
            CollectBorderSegments(space,tr,Matrix3d.Identity,0,segments);
            var horizontals=segments.Where(s=>
            {
                double dx=Math.Abs(s.B.X-s.A.X),dy=Math.Abs(s.B.Y-s.A.Y);
                // Permit small hand-drawn direction errors (about 0.6 degree),
                // while still excluding visibly diagonal drawing geometry.
                return dx>=job.MinFrameWidth*10.0 && dy<=Math.Max(dx,1.0)*.01;
            }).Concat(MergeHorizontalBorderSegments(segments,job.MinFrameWidth,job.MinFrameHeight)).ToList();
            var verticals=segments.Where(s=>
            {
                double dx=Math.Abs(s.B.X-s.A.X),dy=Math.Abs(s.B.Y-s.A.Y);
                return dy>=job.MinFrameHeight*.05 && dx<=Math.Max(dy,1.0)*.01;
            }).ToList();

            foreach(Frame frame in frames)
            {
                Frame anchor=new Frame { Center=frame.Center,Width=frame.Width,Height=frame.Height,Angle=0 };
                double anchorLong=Math.Max(anchor.Width,anchor.Height);
                // The locator may be only a small title block. Do not cap the
                // surrounding sheet to roughly the locator's own dimensions.
                double minSpan=Math.Max(job.MinFrameWidth*2.0,anchorLong*.12);
                // In later supplier drawings the named RCJM1-TK-A0 insertion is
                // only the narrow title/sidebar portion. The real sheet can be
                // 8-12 times wider than that locator. The former 4.5x cap removed
                // the true outer border before scoring and forced a partial page.
                double maxSpan=Math.Max(anchorLong*16.0,job.MinFrameWidth*40.0);
                var nearby=horizontals.Where(s=>
                {
                    double x1=Math.Min(s.A.X,s.B.X),x2=Math.Max(s.A.X,s.B.X);
                    double length=x2-x1,midX=(x1+x2)/2.0,y=(s.A.Y+s.B.Y)/2.0;
                    return length>=minSpan && length<=maxSpan &&
                        Math.Abs(midX-anchor.Center.X)<=maxSpan &&
                        Math.Abs(y-anchor.Center.Y)<=maxSpan;
                }).OrderByDescending(s=>Math.Abs(s.B.X-s.A.X)).Take(400).ToList();
                var localVerticals=verticals.Where(v=>
                {
                    double midX=(v.A.X+v.B.X)/2.0,midY=(v.A.Y+v.B.Y)/2.0;
                    double length=Math.Abs(v.B.Y-v.A.Y);
                    return length>=Math.Max(job.MinFrameHeight,anchorLong*.03) &&
                        Math.Abs(midX-anchor.Center.X)<=maxSpan &&
                        Math.Abs(midY-anchor.Center.Y)<=maxSpan;
                }).ToList();

                // A real closed PLINE is stronger evidence than reconstructed
                // line pairs. Select the largest sheet-like contour containing
                // exactly this locator before evaluating fragmented line borders.
                Frame best=rectangles.Where(r=>
                {
                    double ratio=Math.Max(r.Width,r.Height)/Math.Max(1.0,Math.Min(r.Width,r.Height));
                    return r.Area>anchor.Area*1.20 && r.Width<=maxSpan && r.Height<=maxSpan && ratio<=8.0 &&
                        PointInside(r,frame.Center) &&
                        locatorCenters.Count(p=>PointInsideLoose(r,p))==1 &&
                        FrameInsideLocatorCell(r,frame.Center,locatorCenters);
                }).OrderByDescending(r=>r.Area).FirstOrDefault();
                double bestScore=best==null ? double.MinValue :
                    Math.Log(1.0+best.Area/Math.Max(anchor.Area,1.0))*1000.0+500.0;
                for(int ei=0;ei<nearby.Count;ei++)
                {
                    LineSegment first=nearby[ei];
                    double ax1=Math.Min(first.A.X,first.B.X),ax2=Math.Max(first.A.X,first.B.X);
                    double ay=(first.A.Y+first.B.Y)/2.0;
                    for(int ej=ei+1;ej<nearby.Count;ej++)
                    {
                        LineSegment second=nearby[ej];
                        double bx1=Math.Min(second.A.X,second.B.X),bx2=Math.Max(second.A.X,second.B.X);
                        double by=(second.A.Y+second.B.Y)/2.0;
                        double top=Math.Max(ay,by),bottom=Math.Min(ay,by),height=top-bottom;
                        double overlap=Math.Max(0,Math.Min(ax2,bx2)-Math.Max(ax1,bx1));
                        double shorter=Math.Min(ax2-ax1,bx2-bx1);
                        if(height<job.MinFrameHeight || shorter<=0 || overlap/shorter<.60) continue;
                        double left=Math.Min(ax1,bx1),right=Math.Max(ax2,bx2),width=right-left;
                        double ratio=Math.Max(width,height)/Math.Min(width,height);
                        if(ratio<1.05 || ratio>8.0 || width>maxSpan || height>maxSpan) continue;
                        double sideTol=Math.Max(width*.035,job.MinFrameWidth*.05);
                        double leftCoverage=VerticalCoverageRatio(localVerticals,left,bottom,top,sideTol);
                        double rightCoverage=VerticalCoverageRatio(localVerticals,right,bottom,top,sideTol);
                        // Accept both single and double borders, but both vertical
                        // sides must be materially present. A horizontal internal
                        // line pair alone can never crop a drawing.
                        if(leftCoverage<.25 || rightCoverage<.25 || leftCoverage+rightCoverage<.70) continue;
                        var proposed=new Frame {
                            Center=new Point2d((left+right)/2.0,(top+bottom)/2.0),Width=width,Height=height,
                            Angle=0,SourceDepth=frame.SourceDepth,Source=frame.Source,
                            BlockKey=frame.BlockKey,IsValid=true
                        };
                        // Calibration searches OUTWARD from the locator. It may
                        // enlarge a title block/full frame but must never shrink
                        // either dimension: a smaller internal rectangle is the
                        // exact cause of PDFs containing only part of a sheet.
                        if(proposed.Width<anchor.Width*.90 || proposed.Height<anchor.Height*.90) continue;
                        // The locator must physically lie inside its sheet. A
                        // merely nearby horizontal band can otherwise win and
                        // crop the output to a title cell or internal diagram.
                        if(!PointInside(proposed,frame.Center)) continue;
                        int enclosedLocators=locatorCenters.Count(p=>PointInsideLoose(proposed,p));
                        if(enclosedLocators!=1) continue;
                        if(!FrameInsideLocatorCell(proposed,frame.Center,locatorCenters)) continue;
                        // Spatial partition: a reconstructed border belongs to
                        // the nearest locator only. This prevents one candidate
                        // from spanning two adjacent sheets.
                        Frame nearest=frames.OrderBy(f=>Distance(f.Center,proposed.Center)).FirstOrDefault();
                        if(!object.ReferenceEquals(nearest,frame)) continue;
                        // Never replace a full sheet block with a small closed
                        // table/detail box found inside it.
                        if(proposed.Area<anchor.Area*.20) continue;
                        int textCount=textPoints.Count(p=>PointInside(proposed,p));
                        double anchorDistance=Distance(proposed.Center,anchor.Center)/Math.Max(anchorLong,1.0);
                        double relativeArea=proposed.Area/Math.Max(anchor.Area,1.0);
                        // Prefer the largest credible closure. Dense text inside a
                        // small internal box is only weak supporting evidence.
                        // Exactly-one-locator validation above already rejects a
                        // wrapper spanning adjacent sheets. Within that partition
                        // prefer the largest complete closure, which is the outer
                        // line of a single/double frame rather than its inner line.
                        double score=Math.Log(1.0+Math.Max(relativeArea,0))*1000.0+
                            (leftCoverage+rightCoverage)*100.0+
                            Math.Min(textCount,1000)*.02-anchorDistance*10.0;
                        if(score>bestScore)
                        {
                            bestScore=score;
                            best=proposed;
                        }
                    }
                }
                if(best!=null)
                {
                    frame.Center=best.Center;
                    // Retain imperfect corners, lineweight and text touching the
                    // manually drawn outer frame.
                    frame.Width=best.Width*1.025;
                    frame.Height=best.Height*1.025;
                    frame.Angle=0;
                    // The old outline belongs to the locator block, not to the
                    // newly reconstructed outer border. Keeping it here made the
                    // non-rectangular viewport clip use a small, rotated internal
                    // shape even though the calibrated frame itself was correct.
                    frame.Outline=null;
                    frame.ContourArea=0;
                    EnsureFrameOutline(frame);
                    frame.Decision="valid-block-sheet-border-calibrated";
                    Trace("border calibrated block="+frame.BlockKey+" center="+frame.Center.X+","+frame.Center.Y+
                        " size="+frame.Width+"x"+frame.Height+" score="+bestScore);
                }
                else
                {
                    double anchorRatio=Math.Max(anchor.Width,anchor.Height)/
                        Math.Max(1.0,Math.Min(anchor.Width,anchor.Height));
                    if(anchorRatio>=1.25 && anchorRatio<=1.65 &&
                        anchor.Width>=job.MinFrameWidth && anchor.Height>=job.MinFrameHeight)
                    {
                        // Some suppliers put the complete A-series border and
                        // title bar in the named frame block itself. If no larger
                        // physical closure is available, its near-sqrt(2) extent
                        // is stronger evidence than an unrelated internal box.
                        frame.Angle=0;
                        frame.Outline=null;
                        frame.ContourArea=0;
                        EnsureFrameOutline(frame);
                        frame.Decision="valid-block-sheet-frame-block-extent";
                        frame.IsValid=true;
                        Trace("border fallback to complete A-series frame block="+frame.BlockKey+
                            " size="+frame.Width+"x"+frame.Height);
                    }
                    else
                    {
                        frame.Decision="valid-block-sheet-border-not-found";
                        Trace("border NOT found near block="+frame.BlockKey+"; locator extent rejected");
                    }
                }
            }
        }

        private static double VerticalCoverageRatio(List<LineSegment> verticals,double x,double bottom,double top,double tol)
        {
            var intervals=verticals.Where(v=>
                Math.Abs((v.A.X+v.B.X)/2.0-x)<=tol &&
                Math.Max(v.A.Y,v.B.Y)>bottom && Math.Min(v.A.Y,v.B.Y)<top)
                .Select(v=>new[] { Math.Max(bottom,Math.Min(v.A.Y,v.B.Y)),Math.Min(top,Math.Max(v.A.Y,v.B.Y)) })
                .Where(a=>a[1]>a[0]).OrderBy(a=>a[0]).ToList();
            if(intervals.Count==0) return 0;
            double start=intervals[0][0],end=intervals[0][1],covered=0;
            for(int i=1;i<intervals.Count;i++)
            {
                if(intervals[i][0]<=end+tol) end=Math.Max(end,intervals[i][1]);
                else { covered+=end-start;start=intervals[i][0];end=intervals[i][1]; }
            }
            covered+=end-start;
            return Math.Min(1.0,covered/Math.Max(top-bottom,1e-9));
        }

        private static double HorizontalCoverageRatio(List<LineSegment> horizontals,double y,double left,double right,double tol)
        {
            var intervals=horizontals.Where(h=>
                Math.Abs((h.A.Y+h.B.Y)/2.0-y)<=tol &&
                Math.Max(h.A.X,h.B.X)>left && Math.Min(h.A.X,h.B.X)<right)
                .Select(h=>new[] { Math.Max(left,Math.Min(h.A.X,h.B.X)),Math.Min(right,Math.Max(h.A.X,h.B.X)) })
                .Where(a=>a[1]>a[0]).OrderBy(a=>a[0]).ToList();
            if(intervals.Count==0) return 0;
            double start=intervals[0][0],end=intervals[0][1],covered=0;
            for(int i=1;i<intervals.Count;i++)
            {
                if(intervals[i][0]<=end+tol) end=Math.Max(end,intervals[i][1]);
                else { covered+=end-start;start=intervals[i][0];end=intervals[i][1]; }
            }
            covered+=end-start;
            return Math.Min(1.0,covered/Math.Max(right-left,1e-9));
        }

        private static void CollectBorderSegments(BlockTableRecord space,Transaction tr,Matrix3d transform,
            int depth,List<LineSegment> segments)
        {
            if(depth>4) return;
            foreach(ObjectId id in space)
            {
                Entity ent=tr.GetObject(id,OpenMode.ForRead,false) as Entity;
                if(ent==null || !ent.Visible || !EntityLayerVisible(ent,tr)) continue;
                Line line=ent as Line;
                if(line!=null)
                {
                    Point3d a=line.StartPoint.TransformBy(transform),b=line.EndPoint.TransformBy(transform);
                    segments.Add(new LineSegment { A=new Point2d(a.X,a.Y),B=new Point2d(b.X,b.Y),SourceDepth=depth });
                }
                Polyline pl=ent as Polyline;
                if(pl!=null && pl.NumberOfVertices>=2)
                {
                    int edgeCount=pl.Closed ? pl.NumberOfVertices : pl.NumberOfVertices-1;
                    for(int i=0;i<edgeCount;i++)
                    {
                        if(Math.Abs(pl.GetBulgeAt(i))>1e-8) continue;
                        Point3d a=pl.GetPoint3dAt(i).TransformBy(transform);
                        Point3d b=pl.GetPoint3dAt((i+1)%pl.NumberOfVertices).TransformBy(transform);
                        segments.Add(new LineSegment { A=new Point2d(a.X,a.Y),B=new Point2d(b.X,b.Y),SourceDepth=depth });
                    }
                }
                Polyline2d pl2=ent as Polyline2d;
                if(pl2!=null)
                {
                    var vertices=new List<Vertex2d>();
                    foreach(ObjectId vertexId in pl2)
                    {
                        Vertex2d vertex=tr.GetObject(vertexId,OpenMode.ForRead,false) as Vertex2d;
                        if(vertex!=null) vertices.Add(vertex);
                    }
                    int edgeCount=pl2.Closed ? vertices.Count : vertices.Count-1;
                    for(int i=0;i<edgeCount;i++)
                    {
                        if(Math.Abs(vertices[i].Bulge)>1e-8) continue;
                        Point3d a=vertices[i].Position.TransformBy(transform);
                        Point3d b=vertices[(i+1)%vertices.Count].Position.TransformBy(transform);
                        segments.Add(new LineSegment { A=new Point2d(a.X,a.Y),B=new Point2d(b.X,b.Y),SourceDepth=depth });
                    }
                }
                Polyline3d pl3=ent as Polyline3d;
                if(pl3!=null)
                {
                    var points=new List<Point3d>();
                    foreach(ObjectId vertexId in pl3)
                    {
                        PolylineVertex3d vertex=tr.GetObject(vertexId,OpenMode.ForRead,false) as PolylineVertex3d;
                        if(vertex!=null) points.Add(vertex.Position.TransformBy(transform));
                    }
                    int edgeCount=pl3.Closed ? points.Count : points.Count-1;
                    for(int i=0;i<edgeCount;i++)
                    {
                        Point3d a=points[i],b=points[(i+1)%points.Count];
                        segments.Add(new LineSegment { A=new Point2d(a.X,a.Y),B=new Point2d(b.X,b.Y),SourceDepth=depth });
                    }
                }
                BlockReference br=ent as BlockReference;
                if(br==null) continue;
                try
                {
                    BlockTableRecord definition=(BlockTableRecord)tr.GetObject(br.BlockTableRecord,OpenMode.ForRead);
                    if(!definition.IsLayout && !definition.IsFromExternalReference)
                        CollectBorderSegments(definition,tr,transform*br.BlockTransform,depth+1,segments);
                }
                catch { }
            }
        }

        private static void CollectBlockCandidates(BlockTableRecord space, Transaction tr, Matrix3d parentTransform,
            int depth, List<Frame> candidates, List<Point2d> textPoints)
        {
            if(depth>6) return;
            foreach(ObjectId id in space)
            {
                Entity ent=tr.GetObject(id,OpenMode.ForRead,false) as Entity;
                if(ent==null || !ent.Visible || !EntityLayerVisible(ent,tr)) continue;
                DBText dbText=ent as DBText;
                if(dbText!=null)
                {
                    Point3d p=dbText.Position.TransformBy(parentTransform);
                    textPoints.Add(new Point2d(p.X,p.Y));
                }
                MText mt=ent as MText;
                if(mt!=null)
                {
                    Point3d p=mt.Location.TransformBy(parentTransform);
                    textPoints.Add(new Point2d(p.X,p.Y));
                }
                BlockReference br=ent as BlockReference;
                if(br==null) continue;
                try
                {
                    Extents3d ex=br.GeometricExtents;
                    var corners=new[] {
                        new Point3d(ex.MinPoint.X,ex.MinPoint.Y,0).TransformBy(parentTransform),
                        new Point3d(ex.MinPoint.X,ex.MaxPoint.Y,0).TransformBy(parentTransform),
                        new Point3d(ex.MaxPoint.X,ex.MinPoint.Y,0).TransformBy(parentTransform),
                        new Point3d(ex.MaxPoint.X,ex.MaxPoint.Y,0).TransformBy(parentTransform)
                    };
                    double minX=corners.Min(p=>p.X),maxX=corners.Max(p=>p.X);
                    double minY=corners.Min(p=>p.Y),maxY=corners.Max(p=>p.Y);
                    double width=maxX-minX,height=maxY-minY;
                    if(width>1e-6 && height>1e-6)
                        candidates.Add(new Frame { Center=new Point2d((minX+maxX)/2.0,(minY+maxY)/2.0),
                            Width=width,Height=height,Angle=0,SourceDepth=depth,
                            Source="Block:"+GetBlockName(br,tr),
                            BlockKey=(br.DynamicBlockTableRecord.IsNull ? br.BlockTableRecord : br.DynamicBlockTableRecord).Handle.ToString() });
                }
                catch { }
                try
                {
                    BlockTableRecord definition=(BlockTableRecord)tr.GetObject(br.BlockTableRecord,OpenMode.ForRead);
                    if(!definition.IsLayout && !definition.IsFromExternalReference)
                        CollectBlockCandidates(definition,tr,parentTransform*br.BlockTransform,depth+1,candidates,textPoints);
                }
                catch { }
            }
        }

        private static int CandidatePriority(Frame frame, bool blockFirst)
        {
            bool block=string.Equals(frame.Source,"Block",StringComparison.OrdinalIgnoreCase);
            return blockFirst ? (block ? 0 : 1) : (block ? 1 : 0);
        }

        private static bool ContainsFrame(Frame outer, Frame inner)
        {
            if (AngleDifference(outer.Angle, inner.Angle) > .02) return false;
            double c = Math.Cos(-outer.Angle), s = Math.Sin(-outer.Angle);
            double dx = inner.Center.X - outer.Center.X, dy = inner.Center.Y - outer.Center.Y;
            double x = c * dx - s * dy, y = s * dx + c * dy;
            double ihx = inner.Width / 2.0, ihy = inner.Height / 2.0;
            return Math.Abs(x) + ihx <= outer.Width / 2.0 * 1.005 &&
                   Math.Abs(y) + ihy <= outer.Height / 2.0 * 1.005;
        }

        private static bool PointInside(Frame frame, Point2d point)
        {
            if(frame.Outline!=null && frame.Outline.Count>=3)
            {
                bool inside=false;
                for(int i=0,j=frame.Outline.Count-1;i<frame.Outline.Count;j=i++)
                {
                    Point2d pi=frame.Outline[i],pj=frame.Outline[j];
                    bool crosses=((pi.Y>point.Y)!=(pj.Y>point.Y)) &&
                        point.X < (pj.X-pi.X)*(point.Y-pi.Y)/(pj.Y-pi.Y)+pi.X;
                    if(crosses) inside=!inside;
                }
                return inside;
            }
            double c=Math.Cos(-frame.Angle), s=Math.Sin(-frame.Angle);
            double dx=point.X-frame.Center.X, dy=point.Y-frame.Center.Y;
            return Math.Abs(c*dx-s*dy) <= frame.Width*.495 && Math.Abs(s*dx+c*dy) <= frame.Height*.495;
        }

        private static bool PointInsideLoose(Frame frame,Point2d point)
        {
            // Locator attributes are commonly drawn on, or just inside, the
            // title-border line. Use a small tolerance for ownership tests.
            double c=Math.Cos(-frame.Angle),s=Math.Sin(-frame.Angle);
            double dx=point.X-frame.Center.X,dy=point.Y-frame.Center.Y;
            return Math.Abs(c*dx-s*dy)<=frame.Width*.51 &&
                   Math.Abs(s*dx+c*dy)<=frame.Height*.51;
        }

        private static bool FrameInsideLocatorCell(Frame frame,Point2d owner,List<Point2d> locators)
        {
            if(locators==null || locators.Count<2) return true;
            double c=Math.Cos(frame.Angle),s=Math.Sin(frame.Angle);
            double hx=frame.Width/2.0,hy=frame.Height/2.0;
            var corners=new[] {
                new Point2d(frame.Center.X+c*(-hx)-s*(-hy),frame.Center.Y+s*(-hx)+c*(-hy)),
                new Point2d(frame.Center.X+c*( hx)-s*(-hy),frame.Center.Y+s*( hx)+c*(-hy)),
                new Point2d(frame.Center.X+c*( hx)-s*( hy),frame.Center.Y+s*( hx)+c*( hy)),
                new Point2d(frame.Center.X+c*(-hx)-s*( hy),frame.Center.Y+s*(-hx)+c*( hy))
            };
            foreach(Point2d corner in corners)
            {
                double ownerDistance=Distance(corner,owner);
                foreach(Point2d other in locators)
                {
                    if(Distance(other,owner)<1e-6) continue;
                    // Every corner must remain in the owner's nearest-locator
                    // cell. A small tolerance permits hand-drawn shared borders.
                    if(ownerDistance>Distance(corner,other)*1.02) return false;
                }
            }
            return true;
        }

        private static List<Frame> RemoveOverlappingSheetBoundaries(List<Frame> source)
        {
            // Candidates for the same physical sheet commonly include the outer
            // border, inner border and title/sidebar boundary. Always process the
            // largest first and reject any later candidate whose area is mostly
            // covered by it. Touching adjacent sheets have zero intersection and
            // are retained independently.
            var kept=new List<Frame>();
            foreach(Frame frame in source.OrderByDescending(f=>f.Area))
            {
                bool duplicate=kept.Any(k=>
                    (AngleDifference(k.Angle,frame.Angle)<.04 &&
                     FrameIntersectionOverSmaller(k,frame)>.72) ||
                    ContainsFrame(k,frame));
                if(duplicate)
                {
                    frame.IsValid=false;
                    frame.Decision="rejected-duplicate-sheet-boundary";
                    continue;
                }
                kept.Add(frame);
            }
            return kept;
        }

        private static List<Frame> ConsolidateDetectedFrames(List<Frame> frames,List<Frame> candidates)
        {
            // Final, mode-independent guard. DetectBlockSheets, ContourFirst and
            // fallback paths all pass through here, so an inner border/title bar
            // can never become another page beside its enclosing sheet.
            var kept=new List<Frame>();
            foreach(Frame frame in frames.Where(f=>f!=null && f.IsValid)
                .OrderByDescending(f=>f.Area))
            {
                Frame owner=kept.FirstOrDefault(k=>
                    ContainsFrame(k,frame) ||
                    (AngleDifference(k.Angle,frame.Angle)<.06 &&
                     FrameIntersectionOverSmaller(k,frame)>.55));
                if(owner!=null)
                {
                    frame.IsValid=false;
                    frame.Decision="rejected-universal-overlap-use-largest";
                    continue;
                }
                kept.Add(frame);
            }
            kept.Sort((a,b)=>
            {
                double rowTolerance=Math.Min(a.Height,b.Height)*.35;
                double dy=b.Center.Y-a.Center.Y;
                return Math.Abs(dy)>rowTolerance ? Math.Sign(dy) : a.Center.X.CompareTo(b.Center.X);
            });
            return kept;
        }

        private static double FrameIntersectionOverSmaller(Frame a,Frame b)
        {
            double aminX=a.Center.X-a.Width/2,amaxX=a.Center.X+a.Width/2;
            double aminY=a.Center.Y-a.Height/2,amaxY=a.Center.Y+a.Height/2;
            double bminX=b.Center.X-b.Width/2,bmaxX=b.Center.X+b.Width/2;
            double bminY=b.Center.Y-b.Height/2,bmaxY=b.Center.Y+b.Height/2;
            double w=Math.Max(0,Math.Min(amaxX,bmaxX)-Math.Max(aminX,bminX));
            double h=Math.Max(0,Math.Min(amaxY,bmaxY)-Math.Max(aminY,bminY));
            double smaller=Math.Min(a.Area,b.Area);
            return smaller<=0 ? 0 : w*h/smaller;
        }

        private static bool PaperLayoutHasPrintableContent(BlockTableRecord space,Transaction tr)
        {
            foreach(ObjectId id in space)
            {
                Entity ent=tr.GetObject(id,OpenMode.ForRead,false) as Entity;
                if(ent==null || !ent.Visible || !EntityLayerVisible(ent,tr)) continue;
                Viewport vp=ent as Viewport;
                // Viewport #1 is the mandatory paper-space viewport and is not
                // evidence that the layout has actually been configured.
                if(vp!=null && vp.Number==1) continue;
                if(vp!=null)
                {
                    // An unused layout often contains an extra viewport object that
                    // is switched off or has no paper size. It must not turn an
                    // otherwise blank layout into a PDF page.
                    if(!vp.On || vp.Width<=1e-6 || vp.Height<=1e-6) continue;
                    return true;
                }
                DBText text=ent as DBText;
                if(text!=null && string.IsNullOrWhiteSpace(text.TextString)) continue;
                MText mtext=ent as MText;
                if(mtext!=null && string.IsNullOrWhiteSpace(mtext.Text)) continue;
                // Ignore zero-size points, empty block references and other
                // placeholder entities left by a newly-created layout.
                try
                {
                    Extents3d ext=ent.GeometricExtents;
                    if(Math.Abs(ext.MaxPoint.X-ext.MinPoint.X)<=1e-6 &&
                       Math.Abs(ext.MaxPoint.Y-ext.MinPoint.Y)<=1e-6) continue;
                }
                catch { continue; }
                return true;
            }
            return false;
        }

        private static List<Frame> BuildViewportFallbackFrames(BlockTableRecord space,Transaction tr)
        {
            var viewports=new List<Viewport>();
            var paperExtents=new List<Extents3d>();
            foreach(ObjectId id in space)
            {
                Entity entity=tr.GetObject(id,OpenMode.ForRead,false) as Entity;
                if(entity==null || !entity.Visible || !EntityLayerVisible(entity,tr)) continue;
                Viewport viewport=entity as Viewport;
                if(viewport!=null)
                {
                    if(viewport.Number>1 && viewport.On && viewport.Width>1e-6 && viewport.Height>1e-6)
                        viewports.Add(viewport);
                    continue;
                }
                try { paperExtents.Add(entity.GeometricExtents); } catch { }
            }

            var result=new List<Frame>();
            for(int i=0;i<viewports.Count;i++)
            {
                Viewport vp=viewports[i];
                double minX=vp.CenterPoint.X-vp.Width/2.0;
                double maxX=vp.CenterPoint.X+vp.Width/2.0;
                double minY=vp.CenterPoint.Y-vp.Height/2.0;
                double maxY=vp.CenterPoint.Y+vp.Height/2.0;
                double diagonal=Math.Sqrt(vp.Width*vp.Width+vp.Height*vp.Height);

                // Assign each paper-space border/title entity to its nearest
                // viewport. This preserves a sidebar outside the viewport while
                // preventing a neighbouring sheet from leaking into this PDF.
                foreach(Extents3d ext in paperExtents)
                {
                    double cx=(ext.MinPoint.X+ext.MaxPoint.X)/2.0;
                    double cy=(ext.MinPoint.Y+ext.MaxPoint.Y)/2.0;
                    int nearest=0;
                    double nearestDistance=double.MaxValue;
                    for(int j=0;j<viewports.Count;j++)
                    {
                        double dx=cx-viewports[j].CenterPoint.X;
                        double dy=cy-viewports[j].CenterPoint.Y;
                        double distance=dx*dx+dy*dy;
                        if(distance<nearestDistance) { nearestDistance=distance;nearest=j; }
                    }
                    if(nearest!=i || Math.Sqrt(nearestDistance)>diagonal*1.5) continue;
                    minX=Math.Min(minX,ext.MinPoint.X); maxX=Math.Max(maxX,ext.MaxPoint.X);
                    minY=Math.Min(minY,ext.MinPoint.Y); maxY=Math.Max(maxY,ext.MaxPoint.Y);
                }

                double pad=Math.Max(maxX-minX,maxY-minY)*.005;
                result.Add(new Frame {
                    Center=new Point2d((minX+maxX)/2.0,(minY+maxY)/2.0),
                    Width=maxX-minX+2*pad,Height=maxY-minY+2*pad,
                    Angle=0,SourceDepth=0,Source="PaperViewportFallback",
                    IsValid=true,Decision="valid-paper-viewport-fallback",
                    StrongOuterEvidence=true,IsPaperSpace=true
                });
            }
            return result;
        }

        private static double FrameOverlap(Frame a, Frame b)
        {
            if (AngleDifference(a.Angle,b.Angle)>.02) return 0;
            double aminX=a.Center.X-a.Width/2, amaxX=a.Center.X+a.Width/2;
            double aminY=a.Center.Y-a.Height/2, amaxY=a.Center.Y+a.Height/2;
            double bminX=b.Center.X-b.Width/2, bmaxX=b.Center.X+b.Width/2;
            double bminY=b.Center.Y-b.Height/2, bmaxY=b.Center.Y+b.Height/2;
            double w=Math.Max(0,Math.Min(amaxX,bmaxX)-Math.Max(aminX,bminX));
            double h=Math.Max(0,Math.Min(amaxY,bmaxY)-Math.Max(aminY,bminY));
            double intersection=w*h, union=a.Area+b.Area-intersection;
            return union<=0 ? 0 : intersection/union;
        }

        private static void PrepareMissingFonts(Database db, StringBuilder log)
        {
            int replacedMain=0,replacedBig=0,replacedInline=0,literalQuestions=0;
            try { Application.SetSystemVariable("TEXTFILL", 1); } catch { }
            List<string> fontDirs=FontSearchDirectories(db);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                TextStyleTable styles = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
                foreach (ObjectId id in styles)
                {
                    TextStyleTableRecord style = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    string typeFace=string.Empty;
                    try { typeFace=style.Font.TypeFace??string.Empty; } catch { }
                    // TTF-backed AutoCAD styles commonly keep FileName empty and
                    // store the installed family in Font.TypeFace. Do not mistake
                    // those valid styles for a missing font.
                    bool hasTypeFace=!string.IsNullOrWhiteSpace(typeFace);
                    // A valid Windows TypeFace is sufficient even when the stale
                    // filename says SimSun.ttf while Windows actually provides
                    // simsun.ttc. Conversely an extensionless token such as
                    // O8116901 with no TypeFace is a missing custom CAD font, not
                    // an installed family.
                    bool missingMain=hasTypeFace ? false : !FontExists(style.FileName,fontDirs);
                    bool missingBig=!string.IsNullOrWhiteSpace(style.BigFontFileName) &&
                        !FontExists(style.BigFontFileName,fontDirs);
                    bool isShx=!string.IsNullOrWhiteSpace(style.FileName) &&
                        style.FileName.EndsWith(".shx",StringComparison.OrdinalIgnoreCase);
                    log.AppendLine("FONT STYLE name="+style.Name+" typeface="+typeFace+" main="+(style.FileName??"")+" ["+
                        (missingMain?"MISSING":"FOUND")+"] big="+(style.BigFontFileName??"")+" ["+
                        (string.IsNullOrWhiteSpace(style.BigFontFileName)?"NONE":(missingBig?"MISSING":"FOUND"))+"]");
                    if(!missingMain && !missingBig) continue;
                    style.UpgradeOpen();
                    if(missingMain)
                    {
                        style.FileName=(isShx || style.IsShapeFile)?"txt.shx":"simsun.ttc";
                        replacedMain++;
                    }
                    if(missingBig)
                    {
                        // BigFont encodings are not interchangeable (hzfs.shx is
                        // not compatible with gbcbig.shx). AutoCAD exposes the
                        // decoded text to .NET, so use a Unicode TTF fallback when
                        // the original BigFont is unavailable.
                        style.FileName="simsun.ttc";
                        style.BigFontFileName=string.Empty;
                        replacedBig++;
                    }
                }
                BlockTable blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId blockId in blocks)
                {
                    BlockTableRecord block = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
                    foreach (ObjectId entityId in block)
                    {
                        try
                        {
                            Entity entity=tr.GetObject(entityId,OpenMode.ForRead,false) as Entity;
                            if(entity==null) continue;
                            DBText text=entity as DBText;
                            if(text!=null && Regex.IsMatch(text.TextString??"",@"\?{2,}")) literalQuestions++;
                            MText mt=entity as MText;
                            if(mt==null || string.IsNullOrEmpty(mt.Contents)) continue;
                            if(Regex.IsMatch(mt.Text??"",@"\?{2,}")) literalQuestions++;
                            string fixedContents=Regex.Replace(mt.Contents,@"\\([fF])([^;|]*)(\|[^;]*)?;",
                                delegate(Match match)
                                {
                                    string inlineFont=match.Groups[2].Value.Trim();
                                    if(string.IsNullOrWhiteSpace(Path.GetExtension(inlineFont)) || FontExists(inlineFont,fontDirs))
                                        return match.Value;
                                    replacedInline++;
                                    return "\\"+match.Groups[1].Value+"SimSun"+match.Groups[3].Value+";";
                                });
                            if(fixedContents==mt.Contents) continue;
                            LayerTableRecord layer=(LayerTableRecord)tr.GetObject(mt.LayerId,OpenMode.ForRead);
                            if(layer.IsLocked) continue;
                            mt.UpgradeOpen();
                            mt.Contents=fixedContents;
                        }
                        catch(Autodesk.AutoCAD.Runtime.Exception ex)
                        {
                            if(ex.ErrorStatus!=ErrorStatus.OnLockedLayer) throw;
                        }
                    }
                }
                tr.Commit();
            }
            log.AppendLine("FONT PREFLIGHT replaced-main="+replacedMain+" replaced-big="+replacedBig+
                " replaced-inline="+replacedInline+" literal-question-mark-entities="+literalQuestions);
            if(literalQuestions>0)
                log.AppendLine("WARN UNRECOVERABLE_LITERAL_QUESTION_MARK: source text already contains ??; substitution cannot reconstruct it.");
        }

        private static List<string> FontSearchDirectories(Database db)
        {
            var dirs=new List<string> {
                Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
                Path.Combine(Path.GetDirectoryName(typeof(Database).Assembly.Location),"Fonts"),
                Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"Fonts")
            };
            string assemblyDir=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            DirectoryInfo cursor=new DirectoryInfo(assemblyDir);
            for(int i=0;i<4 && cursor!=null;i++,cursor=cursor.Parent)
                dirs.Add(Path.Combine(cursor.FullName,"Fonts"));
            if(!string.IsNullOrWhiteSpace(db.Filename)) dirs.Add(Path.GetDirectoryName(db.Filename));
            string acad=Environment.GetEnvironmentVariable("ACAD")??string.Empty;
            dirs.AddRange(acad.Split(new[]{';'},StringSplitOptions.RemoveEmptyEntries));
            return dirs.Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool FontExists(string font,IEnumerable<string> directories)
        {
            if(string.IsNullOrWhiteSpace(font)) return false;
            if(Path.IsPathRooted(font) && File.Exists(font)) return true;
            string name=Path.GetFileName(font);
            if(!string.IsNullOrWhiteSpace(Path.GetExtension(name)))
                return directories.Any(dir=>File.Exists(Path.Combine(dir,name)));
            string[] extensions={string.Empty,".shx",".ttf",".ttc",".otf"};
            return directories.Any(dir=>extensions.Any(ext=>File.Exists(Path.Combine(dir,name+ext))));
        }

        private static void CollectRectangles(BlockTableRecord space, Transaction tr, Matrix3d transform, int depth, double minW, double minH, List<Frame> rectangles, List<LineSegment> segments, List<Point2d> textPoints)
        {
            if (depth > 5) return;
            foreach (ObjectId id in space)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (ent == null || !ent.Visible || !EntityLayerVisible(ent, tr)) continue;
                var pl = ent as Polyline;
                Frame rectangle;
                if (pl != null && TryGetRectangle(pl, transform, minW * .80, minH * .80, out rectangle))
                {
                    rectangle.SourceDepth = depth;
                    rectangle.Source = depth > 0 ? "Block" : "Contour";
                    rectangles.Add(rectangle);
                }
                var pl2 = ent as Polyline2d;
                if (pl2 != null && TryGetRectangle(pl2, tr, transform, minW*.80, minH*.80, out rectangle))
                {
                    rectangle.SourceDepth=depth;
                    rectangle.Source=depth>0 ? "Block" : "Contour";
                    rectangles.Add(rectangle);
                }
                var pl3 = ent as Polyline3d;
                if (pl3 != null && TryGetRectangle(pl3, tr, transform, minW*.80, minH*.80, out rectangle))
                {
                    rectangle.SourceDepth=depth;
                    rectangle.Source=depth>0 ? "Block" : "Contour";
                    rectangles.Add(rectangle);
                }
                var line = ent as Line;
                if (line != null)
                {
                    Point3d a = line.StartPoint.TransformBy(transform), b = line.EndPoint.TransformBy(transform);
                    segments.Add(new LineSegment { A = new Point2d(a.X, a.Y), B = new Point2d(b.X, b.Y), SourceDepth = depth });
                }
                var dbText = ent as DBText;
                if (dbText != null)
                {
                    Point3d p=dbText.Position.TransformBy(transform);
                    textPoints.Add(new Point2d(p.X,p.Y));
                }
                var mtext = ent as MText;
                if (mtext != null)
                {
                    Point3d p=mtext.Location.TransformBy(transform);
                    textPoints.Add(new Point2d(p.X,p.Y));
                }
                var br = ent as BlockReference;
                if (br != null)
                {
                    try
                    {
                        BlockTableRecord definition = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                        // Never inspect an XREF's raw definition for sheet borders.
                        // Its entities may be clipped by the host reference, while
                        // recursive traversal sees the complete source drawing and
                        // incorrectly emits every internal frame as a separate page.
                        if (!definition.IsLayout && !definition.IsFromExternalReference)
                        {
                            // Apply the local block transform first through the
                            // accumulated parent transform. The opposite order can
                            // explode nested coordinates into billion-unit phantom
                            // geometry and make real sheet borders fail area filters.
                            Matrix3d nested = transform * br.BlockTransform;
                            CollectRectangles(definition, tr, nested, depth + 1, minW, minH, rectangles, segments, textPoints);
                        }
                    }
                    catch { }
                }
            }
        }

        private static bool EntityLayerVisible(Entity ent, Transaction tr)
        {
            try
            {
                LayerTableRecord layer = (LayerTableRecord)tr.GetObject(ent.LayerId, OpenMode.ForRead);
                return !layer.IsOff && !layer.IsFrozen;
            }
            catch { return true; }
        }

        private static void AddLineRectangles(List<LineSegment> segments, double minW, double minH, List<Frame> rectangles)
        {
            // Permit the tiny common rotation introduced by a block insertion, but
            // only the four-corner closure test below may promote these lines to a
            // frame. An individual diagonal/crossing line is never a border.
            var rawHs = segments.Where(x =>
                Math.Abs(x.A.Y-x.B.Y) <= Math.Max(Math.Abs(x.A.X-x.B.X),1.0)*.001 &&
                Math.Abs(x.A.X-x.B.X) >= minW*.8).ToList();
            var hs = rawHs.Concat(MergeHorizontalBorderSegments(segments,minW,minH)).ToList();
            var allVs = segments.Where(x =>
                Math.Abs(x.A.X-x.B.X) <= Math.Max(Math.Abs(x.A.Y-x.B.Y),1.0)*.001 &&
                Math.Abs(x.A.Y-x.B.Y) >= minH*.03).ToList();
            var vs = allVs.Where(x => Math.Abs(x.A.Y-x.B.Y) >= minH*.8).ToList();
            foreach (LineSegment top in hs)
            {
                double x1=Math.Min(top.A.X,top.B.X), x2=Math.Max(top.A.X,top.B.X), y1=(top.A.Y+top.B.Y)/2.0;
                if (x2-x1 < minW*.8) continue;
                double tol=(x2-x1)*.003;
                foreach (LineSegment bottom in hs)
                {
                    double bx1=Math.Min(bottom.A.X,bottom.B.X), bx2=Math.Max(bottom.A.X,bottom.B.X), y2=(bottom.A.Y+bottom.B.Y)/2.0;
                    double height=Math.Abs(y2-y1);
                    if (y2 >= y1 || height < minH*.8 || Math.Abs(bx1-x1)>tol || Math.Abs(bx2-x2)>tol) continue;
                    LineSegment left=vs.FirstOrDefault(v =>
                        Math.Abs((v.A.X+v.B.X)/2.0-x1)<=tol &&
                        Math.Abs(Math.Min(v.A.Y,v.B.Y)-y2)<=tol &&
                        Math.Abs(Math.Max(v.A.Y,v.B.Y)-y1)<=tol);
                    LineSegment right=vs.FirstOrDefault(v =>
                        Math.Abs((v.A.X+v.B.X)/2.0-x2)<=tol &&
                        Math.Abs(Math.Min(v.A.Y,v.B.Y)-y2)<=tol &&
                        Math.Abs(Math.Max(v.A.Y,v.B.Y)-y1)<=tol);
                    bool leftClosed = left != null || CoversVerticalSide(allVs,x1,y2,y1,tol);
                    bool rightClosed = right != null || CoversVerticalSide(allVs,x2,y2,y1,tol);
                    if (leftClosed && rightClosed) rectangles.Add(new Frame {
                        Center=new Point2d((x1+x2)/2.0,(y1+y2)/2.0),
                        Width=x2-x1, Height=height, Angle=0,
                        SourceDepth=Math.Max(top.SourceDepth,bottom.SourceDepth),
                        Source=Math.Max(top.SourceDepth,bottom.SourceDepth)>0 ? "Block" : "Contour" });
                }
            }
        }

        private static void AddDoubleHorizontalBandRectangles(List<LineSegment> segments, double minW, double minH, List<Frame> rectangles)
        {
            double existingMaxWidth=rectangles.Select(r=>r.Width).DefaultIfEmpty(minW).Max();
            var horizontal=segments.Where(s =>
            {
                double dx=Math.Abs(s.B.X-s.A.X),dy=Math.Abs(s.B.Y-s.A.Y);
                return dx>=minW*.8 && dy<=Math.Max(dx,1.0)*.001;
            }).Concat(MergeHorizontalBorderSegments(segments,minW,minH)).ToList();
            if(horizontal.Count<4) return;

            // Do not derive the threshold from the single longest entity: long
            // cable/grid lines can be much longer than a real title frame. Keep
            // a bounded set of long near-horizontal spans instead, then let the
            // matching endpoints and double-border evidence prove the frame.
            double minimumSpan=Math.Max(minW*5.0,minW);
            var longEdges=horizontal.Where(s=>Math.Abs(s.B.X-s.A.X)>=minimumSpan)
                .OrderByDescending(s=>Math.Abs(s.B.X-s.A.X)).Take(1500).ToList();

            for(int i=0;i<longEdges.Count;i++)
            {
                LineSegment a=longEdges[i];
                double ax1=Math.Min(a.A.X,a.B.X),ax2=Math.Max(a.A.X,a.B.X),ay=(a.A.Y+a.B.Y)/2.0;
                double width=ax2-ax1;
                // A title block may interrupt/shorten one horizontal edge. Allow
                // a modest endpoint difference while still requiring the two
                // spans to describe substantially the same outer rectangle.
                double endpointTol=Math.Max(width*.02,minW*.02);
                for(int j=i+1;j<longEdges.Count;j++)
                {
                    LineSegment b=longEdges[j];
                    double bx1=Math.Min(b.A.X,b.B.X),bx2=Math.Max(b.A.X,b.B.X),by=(b.A.Y+b.B.Y)/2.0;
                    double height=Math.Abs(ay-by);
                    if(height<minH || Math.Abs(bx1-ax1)>endpointTol || Math.Abs(bx2-ax2)>endpointTol) continue;
                    double top=Math.Max(ay,by),bottom=Math.Min(ay,by);
                    double insetMin=Math.Max(height*.0002,minH*.01);
                    double insetMax=height*.08;
                    bool innerNearTop=horizontal.Any(s=>
                    {
                        double y=(s.A.Y+s.B.Y)/2.0,x1=Math.Min(s.A.X,s.B.X),x2=Math.Max(s.A.X,s.B.X);
                        return top-y>=insetMin && top-y<=insetMax && x2-x1>=width*.80 &&
                            x1>=ax1-endpointTol && x2<=ax2+endpointTol;
                    });
                    bool innerNearBottom=horizontal.Any(s=>
                    {
                        double y=(s.A.Y+s.B.Y)/2.0,x1=Math.Min(s.A.X,s.B.X),x2=Math.Max(s.A.X,s.B.X);
                        return y-bottom>=insetMin && y-bottom<=insetMax && x2-x1>=width*.80 &&
                            x1>=ax1-endpointTol && x2<=ax2+endpointTol;
                    });
                    bool completeDoubleBand=innerNearTop && innerNearBottom;
                    // Controlled fallback for drawings whose inner border is
                    // interrupted by nested title blocks: the outer span must be
                    // overwhelmingly larger than every previously reconstructed
                    // internal rectangle. This promotes the sheet perimeter, not
                    // small equipment/table geometry.
                    bool dominantOuterSpan=width>=Math.Max(existingMaxWidth*8.0,minW*20.0);
                    if(!completeDoubleBand && !dominantOuterSpan) continue;

                    int depth=Math.Max(a.SourceDepth,b.SourceDepth);
                    rectangles.Add(new Frame {
                        Center=new Point2d((ax1+ax2)/2.0,(top+bottom)/2.0),
                        Width=width,Height=height,Angle=0,SourceDepth=depth,
                        Source=depth>0 ? "Block" : "Contour",
                        StrongOuterEvidence=dominantOuterSpan
                    });
                }
            }
        }

        private static List<LineSegment> MergeHorizontalBorderSegments(List<LineSegment> segments, double minW, double minH)
        {
            // Rebuild borders whose horizontal edge was split at title-block or
            // block boundaries. Group by direction and infinite-line intercept,
            // then merge only touching/near-touching X intervals.
            double interceptTol=Math.Max(minH*.002,1e-4);
            double gapTol=Math.Max(minW*.01,1e-4);
            var source=segments.Where(s =>
            {
                double dx=s.B.X-s.A.X, dy=s.B.Y-s.A.Y;
                return Math.Abs(dx)>=minW*.05 && Math.Abs(dy)<=Math.Max(Math.Abs(dx),1.0)*.001;
            }).Select(s =>
            {
                double dx=s.B.X-s.A.X, slope=(s.B.Y-s.A.Y)/dx;
                double intercept=s.A.Y-slope*s.A.X;
                return new { Segment=s, Slope=slope, Intercept=intercept,
                    X1=Math.Min(s.A.X,s.B.X), X2=Math.Max(s.A.X,s.B.X) };
            }).GroupBy(x => new {
                S=(long)Math.Round(x.Slope/1e-6),
                I=(long)Math.Round(x.Intercept/interceptTol)
            });
            var merged=new List<LineSegment>();
            foreach(var group in source)
            {
                var ordered=group.OrderBy(x=>x.X1).ToList();
                if(ordered.Count==0) continue;
                double start=ordered[0].X1, end=ordered[0].X2;
                double slope=ordered[0].Slope, intercept=ordered[0].Intercept;
                int depth=ordered[0].Segment.SourceDepth;
                for(int i=1;i<=ordered.Count;i++)
                {
                    if(i<ordered.Count && ordered[i].X1<=end+gapTol)
                    {
                        end=Math.Max(end,ordered[i].X2);
                        depth=Math.Max(depth,ordered[i].Segment.SourceDepth);
                        continue;
                    }
                    if(end-start>=minW*.8)
                        merged.Add(new LineSegment {
                            A=new Point2d(start,slope*start+intercept),
                            B=new Point2d(end,slope*end+intercept), SourceDepth=depth });
                    if(i<ordered.Count)
                    {
                        start=ordered[i].X1; end=ordered[i].X2;
                        slope=ordered[i].Slope; intercept=ordered[i].Intercept;
                        depth=ordered[i].Segment.SourceDepth;
                    }
                }
            }
            return merged;
        }

        private static bool CoversVerticalSide(List<LineSegment> verticals, double x, double bottom, double top, double tol)
        {
            // Title blocks often split the right sheet edge into several collinear
            // LINE entities. Merge only segments which stay inside the two corners;
            // a construction/grid line extending beyond either corner is rejected.
            var intervals = verticals.Where(v =>
                Math.Abs((v.A.X+v.B.X)/2.0-x)<=tol &&
                Math.Min(v.A.Y,v.B.Y)>=bottom-tol &&
                Math.Max(v.A.Y,v.B.Y)<=top+tol)
                .Select(v => new[] { Math.Max(bottom,Math.Min(v.A.Y,v.B.Y)), Math.Min(top,Math.Max(v.A.Y,v.B.Y)) })
                .Where(a => a[1]>a[0]).OrderBy(a=>a[0]).ToList();
            if (intervals.Count == 0 || intervals[0][0] > bottom+tol) return false;
            double covered = intervals[0][1];
            for (int i=1; i<intervals.Count && covered<top-tol; i++)
            {
                if (intervals[i][0] > covered+tol) return false;
                covered=Math.Max(covered,intervals[i][1]);
            }
            return covered>=top-tol;
        }

        private static bool HasInnerBorderEvidence(Frame outer, List<LineSegment> segments, double minW, double minH)
        {
            // The inner title-frame border is frequently split into many LINEs by
            // nested blocks. It therefore need not first form a standalone Frame.
            // Prove it directly from four near-axis line chains just inside the
            // already closed outer rectangle. Diagonal drawing lines are excluded
            // before any coverage test and can never become frame boundaries.
            if (Math.Abs(outer.Angle) > .001) return false;
            double left=outer.Center.X-outer.Width/2.0, right=outer.Center.X+outer.Width/2.0;
            double bottom=outer.Center.Y-outer.Height/2.0, top=outer.Center.Y+outer.Height/2.0;
            double minInsetX=Math.Max(outer.Width*.0002, minW*.01);
            double maxInsetX=outer.Width*.08;
            double minInsetY=Math.Max(outer.Height*.0002, minH*.01);
            double maxInsetY=outer.Height*.08;
            double joinTol=Math.Max(Math.Min(outer.Width,outer.Height)*.003, Math.Max(minW,minH)*.01);

            var hs=segments.Where(s =>
                Math.Abs(s.A.Y-s.B.Y)<=Math.Max(Math.Abs(s.A.X-s.B.X),1.0)*.001 &&
                Math.Abs(s.A.X-s.B.X)>=minW*.03).ToList();
            var vs=segments.Where(s =>
                Math.Abs(s.A.X-s.B.X)<=Math.Max(Math.Abs(s.A.Y-s.B.Y),1.0)*.001 &&
                Math.Abs(s.A.Y-s.B.Y)>=minH*.03).ToList();

            var innerTops=hs.Select(s=>(s.A.Y+s.B.Y)/2.0).Where(y => top-y>=minInsetY && top-y<=maxInsetY).ToList();
            var innerBottoms=hs.Select(s=>(s.A.Y+s.B.Y)/2.0).Where(y => y-bottom>=minInsetY && y-bottom<=maxInsetY).ToList();
            var innerLefts=vs.Select(s=>(s.A.X+s.B.X)/2.0).Where(x => x-left>=minInsetX && x-left<=maxInsetX).ToList();
            var innerRights=vs.Select(s=>(s.A.X+s.B.X)/2.0).Where(x => right-x>=minInsetX && right-x<=maxInsetX).ToList();

            foreach(double yTop in innerTops)
            foreach(double yBottom in innerBottoms)
            {
                if (yTop<=yBottom || !CoversHorizontalSide(hs,yTop,left,right,joinTol) ||
                    !CoversHorizontalSide(hs,yBottom,left,right,joinTol)) continue;
                foreach(double xLeft in innerLefts)
                foreach(double xRight in innerRights)
                {
                    if (xRight<=xLeft) continue;
                    if (CoversVerticalSide(vs,xLeft,yBottom,yTop,joinTol) &&
                        CoversVerticalSide(vs,xRight,yBottom,yTop,joinTol)) return true;
                }
            }
            return false;
        }

        private static bool CoversHorizontalSide(List<LineSegment> horizontals, double y, double left, double right, double tol)
        {
            var intervals=horizontals.Where(h =>
                Math.Abs((h.A.Y+h.B.Y)/2.0-y)<=tol &&
                Math.Min(h.A.X,h.B.X)>=left-tol && Math.Max(h.A.X,h.B.X)<=right+tol)
                .Select(h=>new[] { Math.Max(left,Math.Min(h.A.X,h.B.X)), Math.Min(right,Math.Max(h.A.X,h.B.X)) })
                .Where(a=>a[1]>a[0]).OrderBy(a=>a[0]).ToList();
            if(intervals.Count==0) return false;
            // Inner borders may stop at the title block; require broad coverage,
            // not an exact match to the outer corners.
            double covered=intervals[0][1], total=intervals[0][1]-intervals[0][0];
            for(int i=1;i<intervals.Count;i++)
            {
                if(intervals[i][0]>covered+tol) continue;
                if(intervals[i][1]>covered) { total+=intervals[i][1]-Math.Max(covered,intervals[i][0]); covered=intervals[i][1]; }
            }
            return total >= (right-left)*.82;
        }

        private static bool TryGetRectangle(Polyline pl, Matrix3d transform, double minW, double minH, out Frame frame)
        {
            frame = null;
            if (pl.NumberOfVertices < 4 || pl.NumberOfVertices > 100) return false;
            var points=new List<Point2d>();
            for (int i = 0; i < pl.NumberOfVertices; i++)
            {
                if (Math.Abs(pl.GetBulgeAt(i)) > 1e-8) return false;
                Point3d world = pl.GetPoint3dAt(i).TransformBy(transform);
                points.Add(new Point2d(world.X, world.Y));
            }
            return TryGetRectangleFromPoints(points,pl.Closed,minW,minH,out frame);
        }

        private static bool TryGetRectangle(Polyline2d pl, Transaction tr, Matrix3d transform, double minW, double minH, out Frame frame)
        {
            var points=new List<Point2d>();
            foreach(ObjectId id in pl)
            {
                Vertex2d vertex=tr.GetObject(id,OpenMode.ForRead,false) as Vertex2d;
                if(vertex==null || Math.Abs(vertex.Bulge)>1e-8) { frame=null; return false; }
                Point3d world=vertex.Position.TransformBy(transform);
                points.Add(new Point2d(world.X,world.Y));
            }
            return TryGetRectangleFromPoints(points,pl.Closed,minW,minH,out frame);
        }

        private static bool TryGetRectangle(Polyline3d pl, Transaction tr, Matrix3d transform, double minW, double minH, out Frame frame)
        {
            var points=new List<Point2d>();
            foreach(ObjectId id in pl)
            {
                PolylineVertex3d vertex=tr.GetObject(id,OpenMode.ForRead,false) as PolylineVertex3d;
                if(vertex==null) continue;
                Point3d world=vertex.Position.TransformBy(transform);
                points.Add(new Point2d(world.X,world.Y));
            }
            return TryGetRectangleFromPoints(points,pl.Closed,minW,minH,out frame);
        }

        private static bool TryGetRectangleFromPoints(List<Point2d> input, bool declaredClosed, double minW, double minH, out Frame frame)
        {
            frame=null;
            if(input==null || input.Count<4) return false;
            var points=new List<Point2d>(input);
            double scale=Math.Max(points.Max(q=>q.X)-points.Min(q=>q.X),points.Max(q=>q.Y)-points.Min(q=>q.Y));
            double closeTol=Math.Max(scale*1e-6,1e-6);
            if(Distance(points[0],points[points.Count-1])<=closeTol) points.RemoveAt(points.Count-1);
            else if(!declaredClosed) return false;

            // Remove duplicate and collinear intermediate vertices. A rectangular
            // frame is often split into several vertices at title-block joins.
            bool changed=true;
            while(changed && points.Count>4)
            {
                changed=false;
                for(int i=0;i<points.Count;i++)
                {
                    Point2d prev=points[(i+points.Count-1)%points.Count];
                    Point2d cur=points[i];
                    Point2d next=points[(i+1)%points.Count];
                    Vector2d a=cur-prev,b=next-cur;
                    if(a.Length<=closeTol || b.Length<=closeTol ||
                       Math.Abs(a.X*b.Y-a.Y*b.X)<=a.Length*b.Length*.001)
                    {
                        points.RemoveAt(i); changed=true; break;
                    }
                }
            }
            if(points.Count!=4) return false;
            Point2d[] p=points.ToArray();
            var e = new Vector2d[4];
            var len = new double[4];
            for (int i = 0; i < 4; i++) { e[i] = p[(i + 1) % 4] - p[i]; len[i] = e[i].Length; if (len[i] < 1e-6) return false; }
            // Strict rectangle test: adjacent edges must be perpendicular and
            // opposite edges equal. This rejects irregular/slanted closed shapes.
            for (int i = 0; i < 4; i++) if (Math.Abs(e[i].DotProduct(e[(i + 1) % 4])) / (len[i] * len[(i + 1) % 4]) > .002) return false;
            if (Math.Abs(len[0] - len[2]) / Math.Max(len[0], len[2]) > .002 || Math.Abs(len[1] - len[3]) / Math.Max(len[1], len[3]) > .002) return false;
            double width = len[0], height = len[1], angle = Math.Atan2(e[0].Y, e[0].X);
            if (height > width) { double t = width; width = height; height = t; angle += Math.PI / 2.0; }
            if (width < minW || height < minH) return false;
            while (angle >= Math.PI / 2.0) angle -= Math.PI;
            while (angle < -Math.PI / 2.0) angle += Math.PI;
            double centerX=p.Average(q=>q.X),centerY=p.Average(q=>q.Y);
            frame = new Frame {
                Center = new Point2d(centerX,centerY),Width=width,Height=height,Angle=angle,
                Outline=new List<Point2d>(p)
            };
            return true;
        }

        private static bool IsInnerBorder(Frame outer, Frame inner)
        {
            if (inner.Area >= outer.Area) return false;
            double rw = inner.Width / outer.Width, rh = inner.Height / outer.Height;
            if (rw < .84 || rw > .997 || rh < .80 || rh > .997) return false;
            if (AngleDifference(outer.Angle, inner.Angle) > .01) return false;
            return Distance(outer.Center, inner.Center) < Math.Max(outer.Width, outer.Height) * .025;
        }

        private static double Distance(Point2d a, Point2d b) { double x=a.X-b.X, y=a.Y-b.Y; return Math.Sqrt(x*x+y*y); }
        private static double AngleDifference(double a, double b)
        {
            double d = Math.Abs(a-b) % (Math.PI/2.0);
            return Math.Min(d, Math.PI/2.0-d);
        }

        private static string GetBlockName(BlockReference br, Transaction tr)
        {
            try { return ((BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead)).Name; }
            catch { return string.Empty; }
        }

        private static string ReadDrawingNumber(BlockReference br,Transaction tr)
        {
            try
            {
                foreach(ObjectId id in br.AttributeCollection)
                {
                    AttributeReference ar=tr.GetObject(id,OpenMode.ForRead,false) as AttributeReference;
                    if(ar==null) continue;
                    string tag=(ar.Tag??string.Empty).ToUpperInvariant().Replace(" ",string.Empty).Replace("_",string.Empty);
                    if(tag.Contains("DRAWINGNO") || tag.Contains("DWGNO") || tag.Contains("SHEETNO") ||
                       tag.Contains("图号") || tag=="DRAWING" || tag=="NUMBER")
                    {
                        string value=(ar.TextString??string.Empty).Trim();
                        if(value.Length>0) return value;
                    }
                }
            }
            catch { }
            return string.Empty;
        }

        private static void EnsureFrameOutline(Frame frame)
        {
            if(frame==null || (frame.Outline!=null && frame.Outline.Count>=3)) return;
            double c=Math.Cos(frame.Angle),s=Math.Sin(frame.Angle),hx=frame.Width/2.0,hy=frame.Height/2.0;
            frame.Outline=new List<Point2d> {
                new Point2d(frame.Center.X+c*(-hx)-s*(-hy),frame.Center.Y+s*(-hx)+c*(-hy)),
                new Point2d(frame.Center.X+c*( hx)-s*(-hy),frame.Center.Y+s*( hx)+c*(-hy)),
                new Point2d(frame.Center.X+c*( hx)-s*( hy),frame.Center.Y+s*( hx)+c*( hy)),
                new Point2d(frame.Center.X+c*(-hx)-s*( hy),frame.Center.Y+s*(-hx)+c*( hy))
            };
        }

        private static bool LooksLikeFrameName(string s)
        {
            s = (s ?? "").ToUpperInvariant();
            return s.Contains("TITLE") || s.Contains("BORDER") || s.Contains("FRAME") ||
                s.Contains("图框") || s.Contains("标题栏") ||
                Regex.IsMatch(s,@"(^|[-_$])TK[-_$]?(A[0-4])?($|[-_$])") ||
                Regex.IsMatch(s,@"(^|[-_$])A[0-4][-_$(]");
        }

        private static double OverlapRatio(Extents2d a, Extents2d b)
        {
            double w = Math.Max(0, Math.Min(a.MaxPoint.X, b.MaxPoint.X) - Math.Max(a.MinPoint.X, b.MinPoint.X));
            double h = Math.Max(0, Math.Min(a.MaxPoint.Y, b.MaxPoint.Y) - Math.Max(a.MinPoint.Y, b.MinPoint.Y));
            double intersection = w * h;
            double smaller = Math.Min((a.MaxPoint.X-a.MinPoint.X)*(a.MaxPoint.Y-a.MinPoint.Y), (b.MaxPoint.X-b.MinPoint.X)*(b.MaxPoint.Y-b.MinPoint.Y));
            return smaller <= 0 ? 0 : intersection / smaller;
        }

        private static string OutputPath(string dwg, Job job, string layout, int index, int count)
        {
            return OutputPath(dwg,job,layout,index,count,null);
        }

        private static string OutputPath(string dwg, Job job, string layout, int index, int count, Frame frame)
        {
            string relativeDir = Path.GetDirectoryName(dwg).Substring(Path.GetFullPath(job.InputRoot).TrimEnd(Path.DirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar);
            string dir = Path.Combine(job.OutputRoot, relativeDir);
            Directory.CreateDirectory(dir);
            if(frame!=null && !string.IsNullOrWhiteSpace(frame.DrawingNumber))
            {
                // Repeated/blank attributes occur in old drawings. A unique
                // suffix prevents one valid sheet from overwriting another.
                string numberedSuffix=count>1 ? "_"+(index+1).ToString("00") : string.Empty;
                return Path.Combine(dir,Safe(Path.GetFileNameWithoutExtension(dwg))+"_"+
                    Safe(layout)+"_"+Safe(frame.DrawingNumber)+numberedSuffix+".pdf");
            }
            string suffix = count > 1 ? "_" + (index + 1).ToString("00") : "";
            return Path.Combine(dir, Safe(Path.GetFileNameWithoutExtension(dwg)) + "_" + Safe(layout) + suffix + ".pdf");
        }

        private static string Safe(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim().TrimEnd('.');
        }

        private static void Plot(Database db, ObjectId layoutId, Frame frame, string output)
        {
            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting) throw new InvalidOperationException("已有打印任务正在运行。");
            if (frame != null)
            {
                if(frame.IsPaperSpace)
                {
                    try
                    {
                        PlotPaperSpaceDirect(db,layoutId,frame,output);
                    }
                    catch(Autodesk.AutoCAD.Runtime.Exception ex)
                    {
                        // AutoCAD 2018 rejects Window plotting on some layouts
                        // with eInvalidInput even though the same frame geometry
                        // is valid. Isolate exactly this sheet's paper entities
                        // and viewports, then plot Extents from a clean layout.
                        Trace("paper direct failed "+ex.ErrorStatus+"; isolated-layout fallback");
                        try { if(File.Exists(output)) File.Delete(output); } catch { }
                        PlotPaperSpaceWindow(db,layoutId,frame,output);
                    }
                }
                else
                {
                    // AutoCAD 2018 has a native access-violation bug when a small
                    // legacy/model-space drawing is regenerated through a newly
                    // created PaperSpace viewport.  Plot the current model display
                    // directly for ordinary millimetre-sized, axis-aligned sheets;
                    // this still uses the detected outer frame as the display view
                    // and avoids creating the crashing temporary viewport.
                    bool safeDirectDisplay=Math.Abs(frame.Angle)<1e-8 &&
                        frame.Width<=5000 && frame.Height<=5000;
                    if(safeDirectDisplay) PlotModelFrameDirect(db,frame,output);
                    else PlotFrameThroughPaperViewport(db, frame, output);
                }
                return;
            }
            bool isModel = IsModelLayout(db, layoutId);
            db.TileMode = isModel;
            if (isModel && frame != null) SetModelFrameView(db, frame);
            string layoutName;
            using (Transaction currentTr = db.TransactionManager.StartOpenCloseTransaction())
            {
                layoutName = ((Layout)currentTr.GetObject(layoutId, OpenMode.ForRead)).LayoutName;
                currentTr.Commit();
            }
            // PlotSettingsValidator in AutoCAD 2018 validates Window against the
            // current layout, not merely PlotInfo.Layout. Explicitly activate it.
            LayoutManager.Current.CurrentLayout = layoutName;
            using (var ps = new PlotSettings(isModel))
            {
                Layout layout;
                using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
                    // A fresh model PlotSettings defaults to Display. Do not copy
                    // the legacy model settings: in these DWGs every explicit
                    // Window/View plot type is rejected with eInvalidInput.
                    if (frame == null) ps.CopyFrom(layout);
                    tr.Commit();
                }
                string savedMedia=ps.CanonicalMediaName;
                PlotRotation savedRotation=ps.PlotRotation;
                PlotSettingsValidator v = PlotSettingsValidator.Current;
                if (frame != null)
                {
                    v.SetPlotConfigurationName(ps, "DWG To PDF.pc3", null);
                    v.RefreshLists(ps);
                }
                if (frame != null)
                {
                    if (ps.PlotType != Autodesk.AutoCAD.DatabaseServices.PlotType.Display)
                        throw new InvalidOperationException("新的模型打印设置不是 Display 类型。");
                }
                else v.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Layout);
                v.SetUseStandardScale(ps, true);
                v.SetStdScaleType(ps, StdScaleType.ScaleToFit);
                if (frame != null) v.SetPlotCentered(ps, true);
                // Apply the device after defining the plot area. AutoCAD 2018 can
                // reject Window on drawings whose saved device/media is stale.
                if (frame == null)
                {
                    v.SetPlotConfigurationName(ps, "DWG To PDF.pc3", null);
                    v.RefreshLists(ps);
                    bool mediaAvailable=false;
                    foreach(string media in v.GetCanonicalMediaNameList(ps))
                        if(string.Equals(media,savedMedia,StringComparison.OrdinalIgnoreCase)) { mediaAvailable=true;break; }
                    if(mediaAvailable) v.SetCanonicalMediaName(ps,savedMedia);
                }
                v.SetCurrentStyleSheet(ps, "monochrome.ctb");
                if(frame!=null) SelectMedia(ps, v, frame);
                if (frame != null)
                {
                    // The view above is now landscape. Rotate only the physical
                    // paper when the selected PC3 media is portrait.
                    bool paperLandscape = ps.PlotPaperSize.X >= ps.PlotPaperSize.Y;
                    v.SetPlotRotation(ps, paperLandscape ? PlotRotation.Degrees000 : PlotRotation.Degrees090);
                }
                else v.SetPlotRotation(ps,savedRotation);
                var info = new PlotInfo { Layout = layoutId, OverrideSettings = ps };
                var piv = new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled };
                piv.Validate(info);
                using (PlotEngine pe = PlotFactory.CreatePublishEngine())
                using (var dlg = new PlotProgressDialog(false, 1, true))
                {
                    dlg.OnBeginPlot();
                    pe.BeginPlot(dlg, null);
                    pe.BeginDocument(info, Path.GetFileNameWithoutExtension(output), null, 1, true, output);
                    var page = new PlotPageInfo();
                    pe.BeginPage(page, info, true, null);
                    pe.BeginGenerateGraphics(null);
                    pe.EndGenerateGraphics(null);
                    pe.EndPage(null);
                    pe.EndDocument(null);
                    pe.EndPlot(null);
                    dlg.OnEndPlot();
                }
            }
        }

        private static void PlotPaperSpaceWindow(Database db,ObjectId layoutId,Frame frame,string output)
        {
            // Plotting a Window directly on a layout containing several model
            // viewports is unreliable in AutoCAD 2018: the window and viewport
            // clips are applied in different display coordinate systems.  Copy
            // the entities belonging to this sheet to an isolated temporary
            // layout and plot its extents instead.
            string tempName="BATCHPDF_PS_"+Guid.NewGuid().ToString("N");
            LayoutManager manager=LayoutManager.Current;
            string previousLayout=manager.CurrentLayout;
            string sourceLayoutName;
            using(Transaction nameTr=db.TransactionManager.StartOpenCloseTransaction())
            {
                sourceLayoutName=((Layout)nameTr.GetObject(layoutId,OpenMode.ForRead)).LayoutName;
                nameTr.Commit();
            }
            // Copy the complete source layout so AutoCAD preserves viewport model
            // links, non-rectangular clips, layer overrides and annotation state.
            // Recreating a Viewport property-by-property produced valid but blank
            // 3 KB PDFs in AutoCAD 2018.
            manager.CopyLayout(sourceLayoutName,tempName);
            ObjectId tempLayoutId;
            using(Transaction idTr=db.TransactionManager.StartOpenCloseTransaction())
            {
                DBDictionary layouts=(DBDictionary)idTr.GetObject(db.LayoutDictionaryId,OpenMode.ForRead);
                tempLayoutId=layouts.GetAt(tempName);
                idTr.Commit();
            }
            try
            {
                // Viewport.On may only be changed while its owning layout is the
                // active PaperSpace layout.  AutoCAD 2018 core console is stricter
                // about this than desktop AutoCAD: merely creating the layout is
                // not enough and used to make every isolated-layout fallback fail
                // with eNotInPaperspace.
                ActivatePaperLayout(db,tempName);
                int copied=IsolateCopiedPaperSheet(db,tempLayoutId,frame);
                if(copied==0) throw new InvalidOperationException("图框范围内没有可打印的PaperSpace实体。");
                // Newly appended paper entities/viewports are not reflected in the
                // layout extents cache in AutoCAD 2018 core console. Plotting
                // Extents here produced a technically valid but empty ~8 KB PDF.
                // The copied sheet is deliberately shifted so its outer frame starts
                // at (0,0); use that deterministic local window instead.
                try { db.UpdateExt(true); } catch { }
                try
                {
                    Editor tempEditor=Application.DocumentManager.MdiActiveDocument.Editor;
                    tempEditor.SwitchToPaperSpace();
                    tempEditor.Regen();
                }
                catch { }
                using(var ps=new PlotSettings(false))
                {
                    using(Transaction tr=db.TransactionManager.StartOpenCloseTransaction())
                    {
                        ps.CopyFrom((Layout)tr.GetObject(tempLayoutId,OpenMode.ForRead));
                        tr.Commit();
                    }
                    PlotSettingsValidator v=PlotSettingsValidator.Current;
                    v.SetPlotConfigurationName(ps,"DWG To PDF.pc3",null);v.RefreshLists(ps);
                    v.SetCurrentStyleSheet(ps,"monochrome.ctb");
                    ps.ShadePlot=PlotSettingsShadePlotType.Wireframe;
                    ps.PlotHidden=false;ps.PlotPlotStyles=true;ps.ShowPlotStyles=true;
                    ps.PrintLineweights=true;ps.PlotTransparency=true;
                    SelectMedia(ps,v,frame);
                    bool paperLandscape=ps.PlotPaperSize.X>=ps.PlotPaperSize.Y;
                    bool frameLandscape=frame.Width>=frame.Height;
                    v.SetPlotRotation(ps,paperLandscape==frameLandscape ? PlotRotation.Degrees000 : PlotRotation.Degrees090);
                    // AutoCAD 2018 core console rejects PlotType.Window for these
                    // PaperSpace layouts at SetPlotType itself. Extents is reliable
                    // after CopyPaperSheetToLayout has moved the isolated sheet into
                    // positive coordinates and the database/view have been refreshed.
                    v.SetPlotType(ps,Autodesk.AutoCAD.DatabaseServices.PlotType.Extents);
                    v.SetUseStandardScale(ps,true);v.SetStdScaleType(ps,StdScaleType.ScaleToFit);
                    v.SetPlotCentered(ps,true);
                    Trace("paper copied-layout isolated="+copied+" frame="+frame.Width+"x"+frame.Height);
                    var info=new PlotInfo { Layout=tempLayoutId,OverrideSettings=ps };
                    new PlotInfoValidator { MediaMatchingPolicy=MatchingPolicy.MatchEnabled }.Validate(info);
                    using(PlotEngine pe=PlotFactory.CreatePublishEngine())
                    using(var dlg=new PlotProgressDialog(false,1,true))
                    {
                        dlg.OnBeginPlot();pe.BeginPlot(dlg,null);
                        pe.BeginDocument(info,Path.GetFileNameWithoutExtension(output),null,1,true,output);
                        pe.BeginPage(new PlotPageInfo(),info,true,null);
                        pe.BeginGenerateGraphics(null);pe.EndGenerateGraphics(null);
                        pe.EndPage(null);pe.EndDocument(null);pe.EndPlot(null);dlg.OnEndPlot();
                    }
                }
            }
            finally
            {
                // Deleting the current layout is invalid.  Restore the layout that
                // was active before this one-sheet fallback, then remove the
                // temporary layout even when plotting this frame failed.
                try
                {
                    if(String.Equals(previousLayout,tempName,StringComparison.OrdinalIgnoreCase))
                        previousLayout="Model";
                    manager.CurrentLayout=previousLayout;
                    db.TileMode=String.Equals(previousLayout,"Model",StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    try { manager.CurrentLayout="Model"; db.TileMode=true; } catch { }
                }
                try { manager.DeleteLayout(tempName); } catch { }
            }
        }

        private static int IsolateCopiedPaperSheet(Database db,ObjectId layoutId,Frame frame)
        {
            EnsureFrameOutline(frame);
            double minX=frame.Outline.Min(p=>p.X),maxX=frame.Outline.Max(p=>p.X);
            double minY=frame.Outline.Min(p=>p.Y),maxY=frame.Outline.Max(p=>p.Y);
            double pad=Math.Max(frame.Width,frame.Height)*.005;
            Vector3d shift=new Vector3d(-minX+pad,-minY+pad,0);
            int kept=0;
            using(Transaction tr=db.TransactionManager.StartTransaction())
            {
                Layout layout=(Layout)tr.GetObject(layoutId,OpenMode.ForRead);
                BlockTableRecord btr=(BlockTableRecord)tr.GetObject(layout.BlockTableRecordId,OpenMode.ForWrite);
                var erase=new List<ObjectId>();
                var move=new List<ObjectId>();
                foreach(ObjectId id in btr)
                {
                    Entity entity=tr.GetObject(id,OpenMode.ForRead,false) as Entity;
                    if(entity==null) continue;
                    Viewport viewport=entity as Viewport;
                    if(viewport!=null && viewport.Number<=1) continue;
                    if(!PaperEntityBelongsToFrame(entity,minX,minY,maxX,maxY,pad)) erase.Add(id);
                    else { move.Add(id); kept++; }
                }
                foreach(ObjectId id in erase)
                    ((Entity)tr.GetObject(id,OpenMode.ForWrite,false)).Erase();
                foreach(ObjectId id in move)
                {
                    Entity entity=(Entity)tr.GetObject(id,OpenMode.ForWrite,false);
                    Viewport viewport=entity as Viewport;
                    if(viewport!=null) viewport.CenterPoint=viewport.CenterPoint+shift;
                    else entity.TransformBy(Matrix3d.Displacement(shift));
                }
                tr.Commit();
            }
            // Reset paper extents to the isolated sheet. Extents inherited from the
            // copied multi-sheet layout would otherwise scale the selected sheet to
            // an invisible speck or produce an apparently blank page.
            try
            {
                db.Pextmin=new Point3d(0,0,0);
                db.Pextmax=new Point3d(frame.Width+pad*2,frame.Height+pad*2,0);
            }
            catch { }
            return kept;
        }

        private static void ActivatePaperLayout(Database db,string layoutName)
        {
            LayoutManager.Current.CurrentLayout=layoutName;
            db.TileMode=false;
            Editor editor=Application.DocumentManager.MdiActiveDocument.Editor;
            editor.SwitchToPaperSpace();
            editor.Regen();
        }

        private static void PlotPaperSpaceDirect(Database db,ObjectId layoutId,Frame frame,string output)
        {
            string layoutName;
            using(Transaction tr=db.TransactionManager.StartOpenCloseTransaction())
            {
                layoutName=((Layout)tr.GetObject(layoutId,OpenMode.ForRead)).LayoutName;
                tr.Commit();
            }
            LayoutManager.Current.CurrentLayout=layoutName;
            db.TileMode=false;
            Editor ed=Application.DocumentManager.MdiActiveDocument.Editor;
            try { ed.SwitchToPaperSpace(); } catch { }
            EnsureFrameOutline(frame);
            SetPaperFrameView(ed,frame);

            using(var ps=new PlotSettings(false))
            {
                using(Transaction tr=db.TransactionManager.StartOpenCloseTransaction())
                {
                    ps.CopyFrom((Layout)tr.GetObject(layoutId,OpenMode.ForRead));tr.Commit();
                }
                PlotSettingsValidator v=PlotSettingsValidator.Current;
                v.SetPlotConfigurationName(ps,"DWG To PDF.pc3",null);v.RefreshLists(ps);
                v.SetCurrentStyleSheet(ps,"monochrome.ctb");
                ps.ShadePlot=PlotSettingsShadePlotType.Wireframe;ps.PlotHidden=false;
                ps.PlotPlotStyles=true;ps.ShowPlotStyles=true;ps.PrintLineweights=true;ps.PlotTransparency=true;
                SelectMedia(ps,v,frame);
                bool paperLandscape=ps.PlotPaperSize.X>=ps.PlotPaperSize.Y;
                bool frameLandscape=frame.Width>=frame.Height;
                v.SetPlotRotation(ps,paperLandscape==frameLandscape ? PlotRotation.Degrees000 : PlotRotation.Degrees090);
                // PlotType.Window is rejected by AutoCAD 2018 core console for a
                // number of legacy PaperSpace layouts. Display uses the active
                // paper view framed above and, crucially, renders the ORIGINAL
                // viewport instead of a lossy reconstructed copy.
                v.SetPlotType(ps,Autodesk.AutoCAD.DatabaseServices.PlotType.Display);
                v.SetUseStandardScale(ps,true);v.SetStdScaleType(ps,StdScaleType.ScaleToFit);v.SetPlotCentered(ps,true);
                Trace("paper direct DISPLAY layout="+layoutName+" frame="+frame.Width+"x"+frame.Height);
                var info=new PlotInfo { Layout=layoutId,OverrideSettings=ps };
                new PlotInfoValidator { MediaMatchingPolicy=MatchingPolicy.MatchEnabled }.Validate(info);
                using(PlotEngine pe=PlotFactory.CreatePublishEngine())
                using(var dlg=new PlotProgressDialog(false,1,true))
                {
                    dlg.OnBeginPlot();pe.BeginPlot(dlg,null);
                    pe.BeginDocument(info,Path.GetFileNameWithoutExtension(output),null,1,true,output);
                    pe.BeginPage(new PlotPageInfo(),info,true,null);
                    pe.BeginGenerateGraphics(null);pe.EndGenerateGraphics(null);
                    pe.EndPage(null);pe.EndDocument(null);pe.EndPlot(null);dlg.OnEndPlot();
                }
            }
        }

        private static void SetPaperFrameView(Editor editor,Frame frame)
        {
            using(ViewTableRecord view=editor.GetCurrentView())
            {
                double wantedWidth=frame.Width*1.01;
                double wantedHeight=frame.Height*1.01;
                double displayAspect=view.Height>1e-9 ? view.Width/view.Height : wantedWidth/wantedHeight;
                if(displayAspect>wantedWidth/wantedHeight) wantedWidth=wantedHeight*displayAspect;
                else wantedHeight=wantedWidth/displayAspect;
                view.PerspectiveEnabled=false;
                view.ViewDirection=Vector3d.ZAxis;
                view.Target=new Point3d(frame.Center.X,frame.Center.Y,0);
                view.ViewTwist=-frame.Angle;
                view.CenterPoint=Point2d.Origin;
                view.Width=wantedWidth;
                view.Height=wantedHeight;
                editor.SetCurrentView(view);
            }
            editor.Regen();
        }

        private static int CopyPaperSheetToLayout(Database db,ObjectId sourceLayoutId,ObjectId targetLayoutId,Frame frame)
        {
            EnsureFrameOutline(frame);
            double minX=frame.Outline.Min(p=>p.X),maxX=frame.Outline.Max(p=>p.X);
            double minY=frame.Outline.Min(p=>p.Y),maxY=frame.Outline.Max(p=>p.Y);
            double tolerance=Math.Max(frame.Width,frame.Height)*.003;
            int copied=0;
            using(Transaction tr=db.TransactionManager.StartTransaction())
            {
                Layout source=(Layout)tr.GetObject(sourceLayoutId,OpenMode.ForRead);
                Layout target=(Layout)tr.GetObject(targetLayoutId,OpenMode.ForRead);
                BlockTableRecord sourceBtr=(BlockTableRecord)tr.GetObject(source.BlockTableRecordId,OpenMode.ForRead);
                BlockTableRecord targetBtr=(BlockTableRecord)tr.GetObject(target.BlockTableRecordId,OpenMode.ForWrite);
                var erase=new List<ObjectId>();
                foreach(ObjectId id in targetBtr)
                {
                    Viewport vp=tr.GetObject(id,OpenMode.ForRead,false) as Viewport;
                    if(vp!=null && vp.Number>1) erase.Add(id);
                }
                foreach(ObjectId id in erase) ((Entity)tr.GetObject(id,OpenMode.ForWrite)).Erase();
                // AutoCAD 2018 rejects a PaperSpace plot window whose minimum is
                // negative. Reserve the border allowance by shifting the entire
                // copied sheet into the positive quadrant instead.
                double localPad=Math.Max(frame.Width,frame.Height)*.005;
                Vector3d shift=new Vector3d(-minX+localPad,-minY+localPad,0);
                foreach(ObjectId id in sourceBtr)
                {
                    Entity entity=tr.GetObject(id,OpenMode.ForRead,false) as Entity;
                    if(entity==null || entity is Viewport && ((Viewport)entity).Number<=1) continue;
                    if(!PaperEntityBelongsToFrame(entity,minX,minY,maxX,maxY,tolerance)) continue;
                    Viewport sourceViewport=entity as Viewport;
                    Entity clone=sourceViewport!=null
                        ? RecreatePaperViewport(sourceViewport,shift)
                        : entity.Clone() as Entity;
                    if(clone==null) continue;
                    if(sourceViewport==null) clone.TransformBy(Matrix3d.Displacement(shift));
                    targetBtr.AppendEntity(clone);tr.AddNewlyCreatedDBObject(clone,true);copied++;
                    Viewport newViewport=clone as Viewport;
                    if(newViewport!=null)
                    {
                        // Set On only after the viewport belongs to the active
                        // temporary PaperSpace BTR. Setting it before/while another
                        // layout is active raises eNotInPaperspace in accoreconsole.
                        // A viewport copied from a non-current layout can report
                        // On=false even though it displays normally when that source
                        // layout is activated. The isolated sheet requires an active
                        // viewport in order to render its model geometry.
                        newViewport.On=true;
                        newViewport.UpdateDisplay();
                    }
                }
                tr.Commit();
            }
            return copied;
        }

        private static Viewport RecreatePaperViewport(Viewport source,Vector3d shift)
        {
            // Viewport.Clone() does not reliably preserve the model-view link in
            // AutoCAD 2018 core console. Build a new viewport and explicitly copy
            // every parameter that controls the displayed model rectangle.
            var target=new Viewport();
            target.SetDatabaseDefaults();
            target.SetPropertiesFrom(source);
            target.CenterPoint=source.CenterPoint+shift;
            target.Width=source.Width;
            target.Height=source.Height;
            target.ViewTarget=source.ViewTarget;
            target.ViewCenter=source.ViewCenter;
            target.ViewHeight=source.ViewHeight;
            target.ViewDirection=source.ViewDirection;
            target.TwistAngle=source.TwistAngle;
            target.LensLength=source.LensLength;
            target.PerspectiveOn=source.PerspectiveOn;
            target.FrontClipOn=source.FrontClipOn;
            target.BackClipOn=source.BackClipOn;
            target.FrontClipDistance=source.FrontClipDistance;
            target.BackClipDistance=source.BackClipDistance;
            target.CustomScale=source.CustomScale;
            target.Locked=source.Locked;
            target.ShadePlot=source.ShadePlot;
            target.HiddenLinesRemoved=source.HiddenLinesRemoved;
            target.VisualStyleId=source.VisualStyleId;
            return target;
        }

        private static bool PaperEntityBelongsToFrame(Entity entity,double minX,double minY,double maxX,double maxY,double tolerance)
        {
            try
            {
                Extents3d e=entity.GeometricExtents;
                double cx=(e.MinPoint.X+e.MaxPoint.X)/2.0,cy=(e.MinPoint.Y+e.MaxPoint.Y)/2.0;
                // Assignment by centre prevents a long line or neighbouring
                // frame touching this frame from leaking into both PDFs.
                return cx>=minX-tolerance && cx<=maxX+tolerance &&
                       cy>=minY-tolerance && cy<=maxY+tolerance;
            }
            catch { return false; }
        }

        private static void PlotModelFrameDirect(Database db,Frame frame,string output)
        {
            Trace("model isolated display: enter");
            // AutoCAD 2018 core console rejects both View and Window plot types
            // for some legacy model drawings. Display is stable, so temporarily
            // hide entities assigned to neighbouring sheets. The virtual display
            // may be wider than this frame, but that extra area is then empty.
            List<ObjectId> hidden=HideModelEntitiesOutsideFrame(db,frame);
            try
            {
                SetModelFrameView(db,frame);
            db.TileMode=true;
            LayoutManager.Current.CurrentLayout="Model";
            ObjectId modelLayoutId;
            using(Transaction tr=db.TransactionManager.StartOpenCloseTransaction())
            {
                DBDictionary layouts=(DBDictionary)tr.GetObject(db.LayoutDictionaryId,OpenMode.ForRead);
                modelLayoutId=layouts.GetAt("Model");
                tr.Commit();
            }
            using(var ps=new PlotSettings(true))
            {
                // AutoCAD 2018 rejects PlotType.View on a pristine model
                // PlotSettings object (eInvalidInput). Seed it from the active
                // Model layout so the validator has a valid model plot context.
                using(Transaction tr=db.TransactionManager.StartOpenCloseTransaction())
                {
                    Layout modelLayout=(Layout)tr.GetObject(modelLayoutId,OpenMode.ForRead);
                    ps.CopyFrom(modelLayout);
                    tr.Commit();
                }
                PlotSettingsValidator v=PlotSettingsValidator.Current;
                v.SetPlotConfigurationName(ps,"DWG To PDF.pc3",null);
                v.RefreshLists(ps);
                v.SetPlotType(ps,Autodesk.AutoCAD.DatabaseServices.PlotType.Display);
                v.SetUseStandardScale(ps,true);
                v.SetStdScaleType(ps,StdScaleType.ScaleToFit);
                v.SetPlotCentered(ps,true);
                v.SetCurrentStyleSheet(ps,"monochrome.ctb");
                ps.ShadePlot=PlotSettingsShadePlotType.Wireframe;
                ps.PlotHidden=false;
                ps.PlotPlotStyles=true;
                ps.ShowPlotStyles=true;
                ps.PrintLineweights=true;
                ps.PlotTransparency=true;
                SelectMedia(ps,v,frame);
                bool paperLandscape=ps.PlotPaperSize.X>=ps.PlotPaperSize.Y;
                v.SetPlotRotation(ps,paperLandscape==(frame.Width>=frame.Height)
                    ? PlotRotation.Degrees000 : PlotRotation.Degrees090);
                var info=new PlotInfo { Layout=modelLayoutId,OverrideSettings=ps };
                new PlotInfoValidator { MediaMatchingPolicy=MatchingPolicy.MatchEnabled }.Validate(info);
                using(PlotEngine pe=PlotFactory.CreatePublishEngine())
                using(var dlg=new PlotProgressDialog(false,1,true))
                {
                    dlg.OnBeginPlot(); pe.BeginPlot(dlg,null);
                    pe.BeginDocument(info,Path.GetFileNameWithoutExtension(output),null,1,true,output);
                    pe.BeginPage(new PlotPageInfo(),info,true,null);
                    pe.BeginGenerateGraphics(null); pe.EndGenerateGraphics(null);
                    pe.EndPage(null); pe.EndDocument(null); pe.EndPlot(null); dlg.OnEndPlot();
                }
            }
                Trace("model isolated display: plot completed hidden="+hidden.Count);
            }
            finally
            {
                RestoreModelEntityVisibility(db,hidden);
                try { Application.DocumentManager.MdiActiveDocument.Editor.Regen(); } catch { }
            }
        }

        private static List<ObjectId> HideModelEntitiesOutsideFrame(Database db,Frame frame)
        {
            var hidden=new List<ObjectId>();
            double minX=frame.Center.X-frame.Width/2.0;
            double maxX=frame.Center.X+frame.Width/2.0;
            double minY=frame.Center.Y-frame.Height/2.0;
            double maxY=frame.Center.Y+frame.Height/2.0;
            double tolerance=Math.Max(frame.Width,frame.Height)*.0002;
            using(Transaction tr=db.TransactionManager.StartTransaction())
            {
                BlockTable table=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForRead);
                BlockTableRecord model=(BlockTableRecord)tr.GetObject(table[BlockTableRecord.ModelSpace],OpenMode.ForRead);
                foreach(ObjectId id in model)
                {
                    Entity entity=tr.GetObject(id,OpenMode.ForRead,false) as Entity;
                    if(entity==null || !entity.Visible) continue;
                    bool belongs=false;
                    try
                    {
                        Extents3d ext=entity.GeometricExtents;
                        double cx=(ext.MinPoint.X+ext.MaxPoint.X)/2.0;
                        double cy=(ext.MinPoint.Y+ext.MaxPoint.Y)/2.0;
                        belongs=cx>=minX-tolerance && cx<=maxX+tolerance &&
                                cy>=minY-tolerance && cy<=maxY+tolerance;
                    }
                    catch { belongs=true; }
                    if(belongs) continue;
                    entity.UpgradeOpen();
                    entity.Visible=false;
                    hidden.Add(id);
                }
                tr.Commit();
            }
            return hidden;
        }

        private static void RestoreModelEntityVisibility(Database db,List<ObjectId> hidden)
        {
            if(hidden==null || hidden.Count==0) return;
            using(Transaction tr=db.TransactionManager.StartTransaction())
            {
                foreach(ObjectId id in hidden)
                {
                    if(id.IsNull || id.IsErased || !id.IsValid) continue;
                    Entity entity=tr.GetObject(id,OpenMode.ForWrite,false) as Entity;
                    if(entity!=null) entity.Visible=true;
                }
                tr.Commit();
            }
        }

        private static void PlotFrameThroughPaperViewport(Database db, Frame frame, string output)
        {
            Trace("paper viewport: enter");
            // Warm the model graphics cache for THIS sheet before creating the
            // paper viewport. On AutoCAD 2018 core console the first viewport
            // plot otherwise starts while XREF/model graphics are still being
            // generated, so page 1 contains only the already-cached fragment.
            SetModelFrameView(db,frame);
            try { db.UpdateExt(true); } catch { }
            string layoutName = "BATCHPDF_" + Guid.NewGuid().ToString("N");
            LayoutManager manager = LayoutManager.Current;
            ObjectId layoutId = manager.CreateLayout(layoutName);
            Trace("paper viewport: layout created");
            try
            {
                manager.CurrentLayout = layoutName;
                db.TileMode = false;
                using (var ps = new PlotSettings(false))
                {
                    Layout layout;
                    using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                    {
                        layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
                        ps.CopyFrom(layout);
                        tr.Commit();
                    }
                    PlotSettingsValidator v = PlotSettingsValidator.Current;
                    v.SetPlotConfigurationName(ps, "DWG To PDF.pc3", null);
                    v.RefreshLists(ps);
                    v.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Layout);
                    v.SetUseStandardScale(ps, true);
                    v.SetStdScaleType(ps, StdScaleType.ScaleToFit);
                    v.SetPlotRotation(ps, PlotRotation.Degrees000);
                    v.SetCurrentStyleSheet(ps, "monochrome.ctb");
                    ps.ShadePlot=PlotSettingsShadePlotType.Wireframe;
                    ps.PlotHidden=false;
                    ps.PlotPlotStyles=true;
                    ps.ShowPlotStyles=true;
                    ps.PrintLineweights=true;
                    ps.PlotTransparency=true;
                    SelectMedia(ps, v, frame);
                    bool paperLandscape=ps.PlotPaperSize.X>=ps.PlotPaperSize.Y;
                    bool frameLandscape=frame.Width>=frame.Height;
                    v.SetPlotRotation(ps,paperLandscape==frameLandscape
                        ? PlotRotation.Degrees000 : PlotRotation.Degrees090);
                    Trace("paper viewport: media selected");
                    ConfigurePaperViewport(db, layoutId, frame, ps);
                    Trace("paper viewport: viewport configured");
                    try
                    {
                        Editor plotEditor=Application.DocumentManager.MdiActiveDocument.Editor;
                        plotEditor.SwitchToPaperSpace();
                        plotEditor.Regen();
                    }
                    catch { }
                    var info = new PlotInfo { Layout = layoutId, OverrideSettings = ps };
                    new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled }.Validate(info);
                    using (PlotEngine pe = PlotFactory.CreatePublishEngine())
                    using (var dlg = new PlotProgressDialog(false, 1, true))
                    {
                        dlg.OnBeginPlot(); pe.BeginPlot(dlg, null);
                        pe.BeginDocument(info, Path.GetFileNameWithoutExtension(output), null, 1, true, output);
                        pe.BeginPage(new PlotPageInfo(), info, true, null);
                        pe.BeginGenerateGraphics(null); pe.EndGenerateGraphics(null);
                        pe.EndPage(null); pe.EndDocument(null); pe.EndPlot(null); dlg.OnEndPlot();
                    }
                    Trace("paper viewport: plot completed");
                }
            }
            finally
            {
                try { manager.CurrentLayout = "Model"; } catch { }
                try { manager.DeleteLayout(layoutName); } catch { }
                db.TileMode = true;
            }
        }

        private static void ConfigurePaperViewport(Database db, ObjectId layoutId, Frame frame, PlotSettings ps)
        {
            Extents2d margins = ps.PlotPaperMargins;
            double minX = margins.MinPoint.X, minY = margins.MinPoint.Y;
            double maxX = ps.PlotPaperSize.X - margins.MaxPoint.X;
            double maxY = ps.PlotPaperSize.Y - margins.MaxPoint.Y;
            Extents2d printable = new Extents2d(minX, minY, maxX, maxY);
            double availableWidth = maxX - minX;
            double availableHeight = maxY - minY;
            if (availableWidth <= 1 || availableHeight <= 1)
            {
                availableWidth = ps.PlotPaperSize.X;
                availableHeight = ps.PlotPaperSize.Y;
                printable = new Extents2d(0, 0, availableWidth, availableHeight);
            }
            double frameRatio = frame.Width / frame.Height;
            double viewportWidth = availableWidth * .98;
            double viewportHeight = viewportWidth / frameRatio;
            if (viewportHeight > availableHeight * .98)
            {
                viewportHeight = availableHeight * .98;
                viewportWidth = viewportHeight * frameRatio;
            }
            double centerX = (printable.MinPoint.X + printable.MaxPoint.X) / 2.0;
            double centerY = (printable.MinPoint.Y + printable.MaxPoint.Y) / 2.0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Layout layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
                BlockTableRecord paper = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);
                var oldViewportIds = new List<ObjectId>();
                foreach (ObjectId id in paper)
                {
                    Viewport oldViewport = tr.GetObject(id, OpenMode.ForRead, false) as Viewport;
                    if (oldViewport != null && oldViewport.Number > 1) oldViewportIds.Add(id);
                }
                // Never erase while enumerating a BlockTableRecord: AutoCAD 2018
                // can terminate the core process instead of throwing an exception.
                foreach (ObjectId id in oldViewportIds)
                {
                    Viewport oldViewport = (Viewport)tr.GetObject(id, OpenMode.ForWrite);
                    oldViewport.Erase();
                }
                var viewport = new Viewport();
                paper.AppendEntity(viewport);
                tr.AddNewlyCreatedDBObject(viewport, true);
                viewport.SetDatabaseDefaults();
                viewport.CenterPoint = new Point3d(centerX, centerY, 0);
                viewport.Width = viewportWidth;
                viewport.Height = viewportHeight;
                viewport.ViewDirection = Vector3d.ZAxis;
                viewport.ShadePlot = ShadePlotType.Wireframe;
                viewport.HiddenLinesRemoved = false;
                viewport.ViewTarget = new Point3d(frame.Center.X, frame.Center.Y, 0);
                viewport.ViewCenter = Point2d.Origin;
                // Keep the complete outer border inside the viewport. Cropping at
                // the exact centerline of the outer polyline can omit the border
                // itself because of lineweight and PDF clipping precision.
                // Keep the plot boundary essentially identical to the outer
                // frame. A broad safety margin includes fragments of an adjacent
                // touching sheet. The tiny tolerance is only for numerical
                // precision/half-lineweight at the border.
                viewport.ViewHeight = frame.Height * 1.003;
                // The frame angle is its rotation in WCS. A viewport must twist
                // in the opposite direction to make that frame horizontal on
                // paper. Using the same sign doubled the rotation (about 95
                // degrees in the reported DWG), so the rectangular viewport
                // showed only the diagonal portion crossing it.
                viewport.TwistAngle = -frame.Angle;
                viewport.On = true;
                // A newly-created viewport inherits every layer's
                // IsFrozenInNewViewports flag. Those layers can be visible in the
                // source model but silently disappear from the generated PDF.
                // Thaw only this temporary viewport; global Off/Frozen states in
                // the original DWG remain untouched.
                viewport.ThawAllLayersInViewport();
                viewport.Locked = true;
                // All accepted sheets are verified rectangles. The viewport is
                // already the exact rectangular clipping boundary, so applying a
                // second NonRectClip is both unnecessary and dangerous: a stale
                // locator-block outline previously cut the sheet into a small,
                // tilted fragment. Irregular contours are not accepted upstream.
                tr.Commit();
            }
        }

        private static Extents2d GetOuterBorderWindow(Database db, Frame frame)
        {
            double c = Math.Cos(frame.Angle), s = Math.Sin(frame.Angle);
            double hx = frame.Width / 2.0, hy = frame.Height / 2.0;
            var corners = new Point3d[] {
                new Point3d(frame.Center.X + c*hx - s*hy, frame.Center.Y + s*hx + c*hy, 0),
                new Point3d(frame.Center.X + c*hx + s*hy, frame.Center.Y + s*hx - c*hy, 0),
                new Point3d(frame.Center.X - c*hx - s*hy, frame.Center.Y - s*hx + c*hy, 0),
                new Point3d(frame.Center.X - c*hx + s*hy, frame.Center.Y - s*hx - c*hy, 0)
            };
            // NormalizeModelView has already established a WCS top view. Applying
            // the DWG's previously saved DCS transform here a second time shifts
            // and enlarges the window, which was the source of the huge whitespace.
            Point3d first = corners[0];
            double minX=first.X, maxX=first.X, minY=first.Y, maxY=first.Y;
            for (int i=1; i<corners.Length; i++)
            {
                Point3d p = corners[i];
                minX=Math.Min(minX,p.X); maxX=Math.Max(maxX,p.X); minY=Math.Min(minY,p.Y); maxY=Math.Max(maxY,p.Y);
            }
            return new Extents2d(minX, minY, maxX, maxY);
        }

        private static void SetModelFrameView(Database db, Frame frame)
        {
            db.TileMode = true;
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null || doc.Database != db) throw new InvalidOperationException("无法设置当前 DWG 的打印视图。");
            Editor ed = doc.Editor;
            using (ViewTableRecord view = ed.GetCurrentView())
            {
                double viewportAspect = view.Height > 1e-9 ? view.Width / view.Height : frame.Width / frame.Height;
                if (viewportAspect <= 0 || double.IsNaN(viewportAspect) || double.IsInfinity(viewportAspect))
                    viewportAspect = frame.Width / frame.Height;
                double wantedWidth = frame.Width * 1.01;
                double wantedHeight = frame.Height * 1.01;
                // Display plotting uses the core console's virtual viewport aspect
                // ratio. Setting the raw frame width and height lets AutoCAD shrink
                // one dimension and clips the border. Expand the other dimension
                // so the complete frame is always inside the actual viewport.
                if (viewportAspect > wantedWidth / wantedHeight)
                    wantedWidth = wantedHeight * viewportAspect;
                else
                    wantedHeight = wantedWidth / viewportAspect;
                view.PerspectiveEnabled = false;
                view.ViewDirection = Vector3d.ZAxis;
                view.Target = new Point3d(frame.Center.X, frame.Center.Y, 0);
                // Rotate the WCS rectangle back to horizontal in the display.
                // The view twist must cancel, not repeat, the frame angle.
                view.ViewTwist = -frame.Angle;
                view.CenterPoint = Point2d.Origin;
                view.Width = wantedWidth;
                view.Height = wantedHeight;
                ed.SetCurrentView(view);
            }
            // Display plotting reads the current graphics cache. Without a regen,
            // accoreconsole can plot only the entities generated for the previous
            // view, producing missing borders and partially drawn sheets.
            ed.Regen();
        }

        private static string SetFrameView(Database db, Frame frame)
        {
            const string name = "BATCHPDF_FRAME_VIEW";
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ViewTable table = (ViewTable)tr.GetObject(db.ViewTableId, OpenMode.ForRead);
                ViewTableRecord view;
                if (table.Has(name)) view = (ViewTableRecord)tr.GetObject(table[name], OpenMode.ForWrite);
                else
                {
                    table.UpgradeOpen();
                    view = new ViewTableRecord { Name = name };
                    table.Add(view);
                    tr.AddNewlyCreatedDBObject(view, true);
                }
                view.ViewDirection = Vector3d.ZAxis;
                view.Target = new Point3d(frame.Center.X, frame.Center.Y, 0);
                view.CenterPoint = Point2d.Origin;
                // Keep only a numerical/half-lineweight allowance. The previous
                // one-percent expansion visibly included part of an adjacent
                // touching sheet.
                view.Width = frame.Width * 1.0005;
                view.Height = frame.Height * 1.0005;
                view.ViewTwist = -frame.Angle;
                tr.Commit();
            }
            return name;
        }

        private static bool IsModelLayout(Database db, ObjectId layoutId)
        {
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                Layout layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
                bool result = layout.ModelType;
                tr.Commit();
                return result;
            }
        }

        private static void SelectMedia(PlotSettings ps, PlotSettingsValidator v, Frame frame)
        {
            double target = frame != null
                ? Math.Max(frame.Width,frame.Height)/Math.Max(1.0,Math.Min(frame.Width,frame.Height))
                : 1.414;
            string best = null; double score = double.MaxValue;
            foreach (string media in v.GetCanonicalMediaNameList(ps))
            {
                v.SetCanonicalMediaName(ps, media);
                double w = ps.PlotPaperSize.X, h = ps.PlotPaperSize.Y;
                if (w <= 0 || h <= 0) continue;
                double ratio = Math.Max(w,h)/Math.Min(w,h);
                double areaPenalty = w*h < 50000 ? 1.0 : 0.0; // Prefer A3 and larger for drawings.
                bool targetLandscape = frame == null || frame.Width >= frame.Height;
                bool mediaLandscape = w >= h;
                // Orientation dominates minor media-ratio differences. Choosing
                // a portrait form for a landscape frame made the drawing appear
                // tiny in one corner or clipped behind an oversized border.
                double orientationPenalty = targetLandscape == mediaLandscape ? 0.0 : 10.0;
                double s = Math.Abs(ratio-target) + areaPenalty + orientationPenalty;
                if (s < score) { score = s; best = media; }
            }
            if (best != null) v.SetCanonicalMediaName(ps, best);
        }
    }
}
