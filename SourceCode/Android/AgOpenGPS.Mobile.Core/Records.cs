using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AgOpenGPS.Mobile.Core.Guidance;

namespace AgOpenGPS.Mobile.Core
{
    /// <summary>Which jobs to show: any criterion left null means "all".</summary>
    public sealed class JobFilter
    {
        public string FieldId;
        /// <summary>First and last local day, inclusive.</summary>
        public DateTime? FromLocal, ToLocal;
        /// <summary>Work type, compared ignoring case; "" = jobs without a work type.</summary>
        public string WorkType;

        public bool Matches(Job j)
        {
            if (FieldId != null && j.FieldId != FieldId) return false;
            DateTime day = j.StartUtc.ToLocalTime().Date;
            if (FromLocal.HasValue && day < FromLocal.Value.Date) return false;
            if (ToLocal.HasValue && day > ToLocal.Value.Date) return false;
            if (WorkType != null && !string.Equals((j.WorkType ?? "").Trim(), WorkType.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        public List<Job> Apply(IEnumerable<Job> jobs) => jobs.Where(Matches).OrderByDescending(j => j.StartUtc).ToList();

        /// <summary>The whole calendar year (local).</summary>
        public static JobFilter Year(int year) => new JobFilter { FromLocal = new DateTime(year, 1, 1), ToLocal = new DateTime(year, 12, 31) };
    }

    /// <summary>Totals over some jobs.</summary>
    public sealed class JobTotals
    {
        public int Count;
        public double Hectares, Hours, Kilometres;

        public static JobTotals Of(IEnumerable<Job> jobs)
        {
            var t = new JobTotals();
            foreach (Job j in jobs)
            {
                t.Count++;
                t.Hectares += j.Hectares;
                t.Hours += j.WorkingSeconds / 3600.0;
                t.Kilometres += j.WorkingMeters / 1000.0;
            }
            return t;
        }
    }

    /// <summary>One cell of the season summary: what was done with one work type on one field.</summary>
    public sealed class SeasonCell
    {
        public int Jobs;
        public double Hectares, Hours;
        public DateTime LastLocal;
    }

    public sealed class SeasonRow
    {
        public string FieldId;
        public string FieldName = "";
        public string Crop = "";
        /// <summary>Field area from its boundary; NaN without one (or for a deleted field).</summary>
        public double AreaHectares = double.NaN;
        /// <summary>By work type (as in <see cref="SeasonSummary.WorkTypes"/>).</summary>
        public Dictionary<string, SeasonCell> Cells = new Dictionary<string, SeasonCell>(StringComparer.OrdinalIgnoreCase);
        public JobTotals Total = new JobTotals();
    }

    /// <summary>
    /// The season at a glance: every field against every work type used that year, with hectares,
    /// hours and the last date. Fields without jobs are listed too, so what is not done yet shows.
    /// </summary>
    public sealed class SeasonSummary
    {
        public int Year;
        /// <summary>Work types used in the year, in the order of their first use. "" = no work type given.</summary>
        public List<string> WorkTypes = new List<string>();
        public List<SeasonRow> Rows = new List<SeasonRow>();
        public JobTotals Total = new JobTotals();

        public static SeasonSummary Build(int year, IEnumerable<Field> fields, IEnumerable<Job> jobs)
        {
            var sum = new SeasonSummary { Year = year };
            var byField = new Dictionary<string, SeasonRow>();
            foreach (Field f in fields.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var row = new SeasonRow { FieldId = f.Id, FieldName = f.Name, Crop = f.Crop ?? "", AreaHectares = f.HasBoundary ? f.AreaHectares : double.NaN };
                byField[f.Id] = row;
                sum.Rows.Add(row);
            }

            List<Job> inYear = JobFilter.Year(year).Apply(jobs).OrderBy(j => j.StartUtc).ToList();
            foreach (Job j in inYear)
            {
                string type = (j.WorkType ?? "").Trim();
                string known = sum.WorkTypes.FirstOrDefault(w => string.Equals(w, type, StringComparison.OrdinalIgnoreCase));
                if (known == null) sum.WorkTypes.Add(known = type);

                if (j.FieldId == null || !byField.TryGetValue(j.FieldId, out SeasonRow row))
                {
                    // The field was deleted since: keep its jobs under its old name.
                    string key = j.FieldId ?? "?" + j.FieldName;
                    row = new SeasonRow { FieldId = j.FieldId, FieldName = j.FieldName ?? "", Crop = j.Crop ?? "" };
                    byField[key] = row;
                    sum.Rows.Add(row);
                }
                if (!row.Cells.TryGetValue(known, out SeasonCell cell)) row.Cells[known] = cell = new SeasonCell();
                cell.Jobs++;
                cell.Hectares += j.Hectares;
                cell.Hours += j.WorkingSeconds / 3600.0;
                DateTime day = j.StartUtc.ToLocalTime().Date;
                if (day > cell.LastLocal) cell.LastLocal = day;
                row.Total.Count++;
                row.Total.Hectares += j.Hectares;
                row.Total.Hours += j.WorkingSeconds / 3600.0;
                row.Total.Kilometres += j.WorkingMeters / 1000.0;
            }
            sum.Total = JobTotals.Of(inYear);
            return sum;
        }

        /// <summary>Semicolon separated: field, crop, area, then hectares per work type, then totals.</summary>
        public string ToCsv(Func<string, string> t = null)
        {
            t ??= s => s;
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            var head = new List<string> { t("Field"), t("Crop"), t("Area (ha)") };
            head.AddRange(WorkTypes.Select(w => (w.Length == 0 ? t("No work type") : w) + " (ha)"));
            head.Add(t("Total (ha)"));
            head.Add(t("Working time (h:mm)"));
            sb.Append(string.Join(";", head.Select(Csv))).Append("\r\n");
            foreach (SeasonRow r in Rows)
            {
                var cells = new List<string> { Csv(r.FieldName), Csv(r.Crop), double.IsNaN(r.AreaHectares) ? "" : r.AreaHectares.ToString("0.00", ci) };
                foreach (string w in WorkTypes)
                    cells.Add(r.Cells.TryGetValue(w, out SeasonCell c) ? c.Hectares.ToString("0.00", ci) : "");
                cells.Add(r.Total.Hectares.ToString("0.00", ci));
                cells.Add(JobLog.Duration(r.Total.Hours * 3600));
                sb.Append(string.Join(";", cells)).Append("\r\n");
            }
            return sb.ToString();
        }

        internal static string Csv(string s)
        {
            s ??= "";
            return s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
    }

    /// <summary>One line of the spray and fertiliser record: one product in one job.</summary>
    public sealed class InputRecordRow
    {
        public Job Job;
        public JobInput Input;
        public double Hectares => Job.Hectares;
        public double Total => Input.Total(Job.Hectares);
    }

    /// <summary>Totals per product (and unit) over the record.</summary>
    public sealed class ProductTotal
    {
        public string Product = "", Unit = "";
        public double Amount, Hectares;
        public int Uses;
    }

    /// <summary>
    /// The record of products applied (plant protection, fertiliser, seed): date, field, crop, area,
    /// product, dose and total, as farmers have to keep for plant protection products.
    /// </summary>
    public static class InputRecord
    {
        public static List<InputRecordRow> Rows(IEnumerable<Job> jobs) =>
            jobs.Where(j => j.Inputs != null)
                .OrderBy(j => j.StartUtc)
                .SelectMany(j => j.Inputs.Select(i => new InputRecordRow { Job = j, Input = i }))
                .ToList();

        public static List<ProductTotal> Totals(IEnumerable<InputRecordRow> rows)
        {
            var map = new Dictionary<string, ProductTotal>(StringComparer.OrdinalIgnoreCase);
            foreach (InputRecordRow r in rows)
            {
                string key = r.Input.Product.Trim() + "|" + r.Input.TotalUnit;
                if (!map.TryGetValue(key, out ProductTotal t))
                    map[key] = t = new ProductTotal { Product = r.Input.Product.Trim(), Unit = r.Input.TotalUnit };
                t.Amount += r.Total;
                t.Hectares += r.Hectares;
                t.Uses++;
            }
            return map.Values.OrderBy(t => t.Product, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static string ToCsv(IEnumerable<InputRecordRow> rows, Func<string, string> t = null)
        {
            t ??= s => s;
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append(string.Join(";", new[] { "Date", "Field", "Crop", "Work type", "Area (ha)", "Product", "Dose", "Unit",
                "Total", "Total unit", "Water (l/ha)", "Against / purpose", "Start", "End", "Note" }.Select(t))).Append("\r\n");
            foreach (InputRecordRow r in rows)
            {
                Job j = r.Job;
                DateTime s = j.StartUtc.ToLocalTime(), e = j.EndUtc.ToLocalTime();
                sb.Append(s.ToString("yyyy-MM-dd", ci)).Append(';')
                  .Append(SeasonSummary.Csv(j.FieldName)).Append(';')
                  .Append(SeasonSummary.Csv(j.Crop)).Append(';')
                  .Append(SeasonSummary.Csv(j.WorkType)).Append(';')
                  .Append(j.Hectares.ToString("0.00", ci)).Append(';')
                  .Append(SeasonSummary.Csv(r.Input.Product)).Append(';')
                  .Append(r.Input.Dose.ToString("0.###", ci)).Append(';')
                  .Append(r.Input.Unit).Append(';')
                  .Append(r.Total.ToString("0.##", ci)).Append(';')
                  .Append(r.Input.TotalUnit).Append(';')
                  .Append(j.WaterLitersPerHa.HasValue ? j.WaterLitersPerHa.Value.ToString("0", ci) : "").Append(';')
                  .Append(SeasonSummary.Csv(r.Input.Target)).Append(';')
                  .Append(s.ToString("HH:mm", ci)).Append(';')
                  .Append(e.ToString("HH:mm", ci)).Append(';')
                  .Append(SeasonSummary.Csv(j.Note)).Append("\r\n");
            }
            return sb.ToString();
        }
    }
}
