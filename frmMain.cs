using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiagnosticsTools
{
    public partial class frmMain : Form
    {
        public frmMain()
        {
            InitializeComponent();
        }

        Timer Timer;

        Dictionary<int, DiagnosticsClient> diagnosticsCache = new Dictionary<int, DiagnosticsClient>();
        private void frmMain_Load(object sender, EventArgs e)
        {
            //定时刷新列表
            //Timer = new Timer();
            //Timer.Interval = 5000;
            //Timer.Tick += Timer_Tick;
            //Timer.Start();
            PrintProcessStatus();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            PrintProcessStatus();
        }


        private void dgvPros_SelectionChanged(object sender, EventArgs e)
        {
            if (dgvPros.SelectedRows.Count > 0)
            {
                var row = dgvPros.SelectedRows[0];
                if (row.Cells[0].Value != null)
                {
                    var info = Process.GetProcessById((int)row.Cells["pId"].Value);
                    if (info != null && !info.HasExited)
                    {
                        txtInfo.Text = GetProInfo(info);
                        //进程运行信息输出
                        rtbMsg.Clear();
                        if (!diagnosticsCache.ContainsKey(info.Id))
                        {
                            Task.Run(() =>
                            {
                                PrintRuntime(info.Id);
                            });
                        }
                    }
                }
            }
        }

        private void toolStrip1_ItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {
            //异常模拟
            try
            {
                int i = 0;
                int num = 10;
                var ev = num / i;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void contextMenuStrip1_ItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {
            contextMenuStrip1.Hide();
            var pid = (int)dgvPros.CurrentCell.OwningRow.Cells["PId"].Value;
            string name = e.ClickedItem.Name;
            switch (name)
            {
                case "tsmiDump":
                    TriggerCoreDump(pid);
                    break;
                case "tsmiTrace":
                    TraceProcessForDuration(pid, 30);
                    break;
                case "tsmiRef":
                    PrintProcessStatus();
                    break;
                default:
                    break;
            }
        }

        /// <summary>
        /// 获取进程状态：.Net Core 3.0及以上进程
        /// </summary>
        private void PrintProcessStatus()
        {
            int row = dgvPros.CurrentCell == null ? 0 : dgvPros.CurrentCell.RowIndex;
            int col = dgvPros.CurrentCell == null ? 0 : dgvPros.CurrentCell.ColumnIndex;
            var data = DiagnosticsClient.GetPublishedProcesses()
                    .Select(Process.GetProcessById)
                    .Where(process => process != null)
                    .Select(o => { return new { o.Id, o.ProcessName, o.StartTime, o.Threads.Count }; });

            dgvPros.DataSource = data.ToList();
            if (dgvPros.Rows.Count > row)
                dgvPros.CurrentCell = dgvPros.Rows[row].Cells[col];
        }

        private string GetProInfo(Process info)
        {
            StringBuilder stringBuilder = new StringBuilder();
            stringBuilder.Append("进程影象名：" + info.ProcessName + "\r\n");
            stringBuilder.Append("进程ID：" + info.Id + "\r\n");
            stringBuilder.Append("启动线程树：" + info.Threads.Count.ToString() + "\r\n");
            stringBuilder.Append("CPU占用时间：" + info.TotalProcessorTime.ToString() + "\r\n");
            stringBuilder.Append("线程优先级：" + info.PriorityClass.ToString() + "\r\n");
            stringBuilder.Append("启动时间：" + info.StartTime.ToLongTimeString() + "\r\n");
            stringBuilder.Append("专用内存：" + (info.PrivateMemorySize64 / 1024).ToString() + "K" + "\r\n");
            stringBuilder.Append("峰值虚拟内存：" + (info.PeakVirtualMemorySize64 / 1024).ToString() + "K" + "\r\n");
            stringBuilder.Append("峰值分页内存：" + (info.PeakPagedMemorySize64 / 1024).ToString() + "K" + "\r\n");
            stringBuilder.Append("分页系统内存：" + (info.PagedSystemMemorySize64 / 1024).ToString() + "K" + "\r\n");
            stringBuilder.Append("分页内存：" + (info.PagedMemorySize64 / 1024).ToString() + "K" + "\r\n");
            stringBuilder.Append("未分页系统内存：" + (info.NonpagedSystemMemorySize64 / 1024).ToString() + "K" + "\r\n");
            stringBuilder.Append("物理内存：" + (info.WorkingSet64 / 1024).ToString() + "K" + "\r\n");
            stringBuilder.Append("虚拟内存：" + (info.VirtualMemorySize64 / 1024).ToString() + "K");
            return stringBuilder.ToString();
        }

        /// <summary>
        /// 进程运行事件输出：CLR、性能计数器、动态处理（cpu使用率超过90%则抓取dump）
        /// </summary>
        /// <param name="processId">进程id</param>
        /// <param name="threshold">cpu使用率</param>
        private void PrintRuntime(int processId, int threshold = 90)
        {
            if (!diagnosticsCache.ContainsKey(processId))
            {
                var providers = new List<EventPipeProvider>()
                {
                    new EventPipeProvider("Microsoft-Windows-DotNETRuntime",EventLevel.Informational, (long)ClrTraceEventParser.Keywords.Default),
                        //性能计数器：间隔时间为5s
                    new EventPipeProvider("System.Runtime",EventLevel.Informational,(long)ClrTraceEventParser.Keywords.None,
                                                new Dictionary<string, string>() {{ "EventCounterIntervalSec", "5" }})
                };

                DiagnosticsClient client = new DiagnosticsClient(processId);
                diagnosticsCache[processId] = client;
                using (EventPipeSession session = client.StartEventPipeSession(providers, false))
                {
                    var source = new EventPipeEventSource(session.EventStream);

                    source.Clr.All += (TraceEvent obj) =>
                    {
                        if (dgvPros.CurrentRow != null && obj.ProcessID.Equals(dgvPros.CurrentRow.Cells[0].Value))
                        {
                            string msg = $"Clr-{obj.EventName}-";
                            if (obj.PayloadNames.Length > 0)
                            {
                                foreach (var item in obj.PayloadNames)
                                    msg += $"{item}：{ obj.PayloadStringByName(item)}-";
                            }
                            TextAppendLine(msg);
                        }
                    };
                    source.Dynamic.All += (TraceEvent obj) =>
                    {
                        if (dgvPros.CurrentRow != null && obj.ProcessID.Equals(dgvPros.CurrentRow.Cells[0].Value))
                        {
                            string msg = $"Dynamic-{obj.EventName}-{string.Join("|", obj.PayloadNames)}";
                            //性能计数器事件
                            if (obj.EventName.Equals("EventCounters"))
                            {
                                var payloadFields = (IDictionary<string, object>)(obj.PayloadByName(""));
                                if (payloadFields != null)
                                    payloadFields = payloadFields["Payload"] as IDictionary<string, object>;

                                if (payloadFields != null)
                                {
                                    msg = $"Dynamic-{obj.EventName}-{payloadFields["DisplayName"]}：{payloadFields["Mean"]}{payloadFields["DisplayUnits"]}";
                                    TextAppendLine(msg);
                                }
                                //如果CPU使用率超过90%抓取dump
                                if (payloadFields != null && payloadFields["Name"].ToString().Equals("cpu-usage"))
                                {
                                    double cpuUsage = Double.Parse(payloadFields["Mean"].ToString());
                                    if (cpuUsage > (double)threshold)
                                    {
                                        client.WriteDump(DumpType.Normal, "/tmp/minidump.dmp");
                                    }
                                }
                            }
                            else
                            {
                                if (obj.PayloadNames.Length > 0)
                                {
                                    foreach (var item in obj.PayloadNames)
                                        msg += $"{item}：{ obj.PayloadStringByName(item)}-";
                                }
                                TextAppendLine(msg);
                            }
                        }
                    };
                    source.Kernel.All += (TraceEvent obj) =>
                    {
                        if (dgvPros.CurrentRow != null && obj.ProcessID.Equals(dgvPros.CurrentRow.Cells[0].Value))
                        {
                            string msg = $"Kernel-{obj.EventName}-{string.Join("|", obj.PayloadNames)}";
                            TextAppendLine(msg);
                        }
                    };

                    try
                    {
                        source.Process();
                    }
                    catch (Exception e)
                    {
                        string errorMsg = $"错误：{e}";
                        TextAppendLine(errorMsg);
                    }
                }
            }
        }

        private void TextAppendLine(string msg)
        {
            if (rtbMsg.InvokeRequired)
            {
                rtbMsg.Invoke(new Action(() =>
                {
                    rtbMsg.AppendText($"{DateTime.Now} {msg}\r\n");
                }));
            }
            else
            {
                rtbMsg.AppendText($"{DateTime.Now} {msg}\r\n");
            }
        }

        /// <summary>
        /// 抓取Dmp文件
        /// </summary>
        /// <param name="processId"></param>
        private void TriggerCoreDump(int processId)
        {
            saveFileDialog1.Filter = "Dump文件|*.dmp";
            if (saveFileDialog1.ShowDialog() == DialogResult.OK)
            {
                var client = new DiagnosticsClient(processId);
                //Normal = 1,WithHeap = 2,Triage = 3,Full = 4
                client.WriteDump(DumpType.Normal, saveFileDialog1.FileName, true);
            }
        }

        /// <summary>
        /// 写入Trace文件
        /// </summary>
        /// <param name="processId">进程ID</param>
        /// <param name="duration">指定时间范围(单位s)</param>
        private void TraceProcessForDuration(int processId, int duration)
        {
            saveFileDialog1.Filter = "Nettrace文件|*.nettrace";
            if (saveFileDialog1.ShowDialog() == DialogResult.OK)
            {
                var cpuProviders = new List<EventPipeProvider>()
                {
                    new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, (long)ClrTraceEventParser.Keywords.Default),
                    new EventPipeProvider("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational, (long)ClrTraceEventParser.Keywords.None)
                };
                var client = new DiagnosticsClient(processId);
                using (var traceSession = client.StartEventPipeSession(cpuProviders))
                {
                    Task copyTask = Task.Run(async () =>
                    {
                        using (FileStream fs = new FileStream(saveFileDialog1.FileName, FileMode.Create, FileAccess.Write))
                        {
                            await traceSession.EventStream.CopyToAsync(fs);
                        }
                    });

                    copyTask.Wait(duration * 1000);
                    traceSession.Stop();
                }
            }
        }

        private void menuStrip1_ItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {

        }
    }
}
