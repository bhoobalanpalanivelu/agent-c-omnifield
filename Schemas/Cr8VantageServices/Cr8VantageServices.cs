namespace Terrasoft.Configuration.Cr8FieldServiceOps
{
	using System;
	using System.Collections.Generic;
	using System.Globalization;
	using System.Linq;
	using System.Text;
	using Terrasoft.Core;
	using Terrasoft.Core.Configuration;
	using Terrasoft.Core.Entities;

	// ======================================================================================
	// Vantage Field Service — C# services (Part A §6.4, C1–C6).
	// Source code schema: Cr8VantageServices (package Cr8FieldServiceOps).
	//
	// Layout: pure logic first (no Terrasoft types, reviewable line by line against the Python
	// specs in build/vantage/skills-fallback-ootb and build/vantage/tests), then the thin ESQ
	// data-access services that read settings and records and call the pure logic.
	// ======================================================================================

	// ==== PURE-LOGIC BEGIN ====

	/// <summary>Service calendar values; mirrors references/service-calendar.json and the Cr8Vantage* settings.</summary>
	public sealed class Cr8ServiceCalendarConfig
	{
		public IList<string> Days = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
		public IList<string> WindowStarts = new[] { "08:00", "10:00", "12:00", "14:00", "16:00" };
		public int WindowHours = 2;
		public int LeadMinutes = 60;
		public int HorizonBusinessDays = 10;
		public int TargetDaysHigh = 0;
		public int TargetDaysMedium = 2;
		public int TargetDaysLow = 5;
		public string TimezoneLabel = "Central Time";
	}

	/// <summary>A qualified technician (Employee with the Job), already filtered by Job.</summary>
	public sealed class Cr8TechnicianCandidate
	{
		public Guid ContactId;
		public string Name;
	}

	/// <summary>A calendar Activity that blocks its owner, in wall-clock time.</summary>
	public sealed class Cr8BusyPeriod
	{
		public Guid OwnerId;
		public DateTime Start;
		public DateTime End;
	}

	/// <summary>C1 result. Field names match the T2 output parameters (Part A §6.3).</summary>
	public sealed class Cr8SlotOffer
	{
		public string Status;              // ok | none | error
		public string Reason;              // "" when ok; no_qualified_technicians | no_free_window_in_horizon | error text
		public Guid TechnicianContactId;
		public string TechnicianName;
		public DateTime WindowStart;
		public DateTime WindowEnd;
		public string SpokenWindow;
		public string DisplayWindow;
		public bool WithinTarget;
		public string PreferenceMatched;   // yes | no | none
		public int BusinessDaysFromToday;
	}

	/// <summary>A technician's Activity as seen by C5, in wall-clock time. Ids are strings so ordering matches the spec.</summary>
	public sealed class Cr8CalendarActivity
	{
		public string Id;
		public string CaseId;              // null or empty = no case
		public DateTime Start;
		public DateTime End;
	}

	/// <summary>Result of the pure current-job rule.</summary>
	public sealed class Cr8CurrentJobPick
	{
		public string Basis;               // in_progress | most_recent | next_upcoming | none
		public Cr8CalendarActivity Job;
		public int OtherCandidates;
		public int IgnoredNoCase;
		public int IgnoredNotToday;
	}

	/// <summary>C5 result. Field names match the T5 output parameters (Part A §6.3).</summary>
	public sealed class Cr8CurrentJob
	{
		public string Basis;               // in_progress | most_recent | next_upcoming | none | not_a_technician (| error, set by the user task)
		public Guid CaseId;
		public string CaseNumber;
		public string Street;
		public string EquipmentName;
		public string CustomerGivenName;
		public string SpokenWindow;
		public string FeePosition;
		public string Subject;
		public string ExistingSolution;
		public int OtherCandidates;
	}

	/// <summary>Pure scheduling rules: port of find_slots.py (C1, C2, C3, C6).</summary>
	public static class Cr8SchedulingRules
	{
		private static readonly string[] DayCodes = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
		private static readonly string[] DayNames = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
		private static readonly string[] MonthNames = { "January", "February", "March", "April", "May", "June", "July",
			"August", "September", "October", "November", "December" };

		/// <summary>Python weekday(): Monday = 0 … Sunday = 6.</summary>
		private static int PyWeekday(DateTime day) {
			return ((int)day.DayOfWeek + 6) % 7;
		}

		public static bool IsServiceDay(DateTime day, IList<string> days) {
			return days.Contains(DayCodes[PyWeekday(day)]);
		}

		/// <summary>find_slots.normalise priority: case-insensitive High / Medium / Low, else null.</summary>
		public static string NormalizePriority(string priority) {
			switch ((priority ?? string.Empty).Trim().ToLowerInvariant()) {
				case "high": return "High";
				case "medium": return "Medium";
				case "low": return "Low";
				default: return null;
			}
		}

		/// <summary>find_slots.parse_hhmm.</summary>
		public static void ParseHhMm(string value, string field, out int hours, out int minutes) {
			string[] parts = (value ?? string.Empty).Split(':');
			if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hours)
					|| !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes)) {
				throw new ArgumentException(field + " must be HH:MM, got '" + value + "'");
			}
		}

		/// <summary>find_slots.service_days: the first <paramref name="count"/> service days from <paramref name="startDay"/> inclusive.</summary>
		public static List<DateTime> ServiceDays(DateTime startDay, int count, IList<string> days) {
			var result = new List<DateTime>();
			DateTime current = startDay.Date;
			while (result.Count < count) {
				if (IsServiceDay(current, days)) {
					result.Add(current);
				}
				current = current.AddDays(1);
			}
			return result;
		}

		/// <summary>find_slots.business_days_between: service days in (today, targetDay].</summary>
		public static int BusinessDaysBetween(DateTime today, DateTime targetDay, IList<string> days) {
			int count = 0;
			DateTime current = today.Date.AddDays(1);
			while (current <= targetDay.Date) {
				if (IsServiceDay(current, days)) {
					count++;
				}
				current = current.AddDays(1);
			}
			return count;
		}

		/// <summary>Half-open overlap [start, end): a booking ending exactly at a window start does not overlap.</summary>
		public static bool Overlaps(DateTime busyStart, DateTime busyEnd, DateTime start, DateTime end) {
			return busyStart < end && busyEnd > start;
		}

		private static void ClockWords(DateTime moment, out string text, out string half) {
			int hour = moment.Hour % 12 == 0 ? 12 : moment.Hour % 12;
			text = moment.Minute == 0
				? hour.ToString(CultureInfo.InvariantCulture)
				: hour.ToString(CultureInfo.InvariantCulture) + ":" + moment.Minute.ToString("00", CultureInfo.InvariantCulture);
			half = moment.Hour < 12 ? "AM" : "PM";
		}

		/// <summary>Times only: "2 to 4 PM", "11 AM to 1 PM" (current_job.spoken_window).</summary>
		public static string SpokenTimes(DateTime start, DateTime end) {
			string startText, startHalf, endText, endHalf;
			ClockWords(start, out startText, out startHalf);
			ClockWords(end, out endText, out endHalf);
			return startHalf == endHalf
				? startText + " to " + endText + " " + endHalf
				: startText + " " + startHalf + " to " + endText + " " + endHalf;
		}

		/// <summary>find_slots.spoken_window: "today, 2 to 4 PM" · "tomorrow, 8 to 10 AM" · "Monday 28 September, 8 to 10 AM".</summary>
		public static string SpokenWindow(DateTime start, DateTime end, DateTime today) {
			string day;
			if (start.Date == today.Date) {
				day = "today";
			} else if (start.Date == today.Date.AddDays(1)) {
				day = "tomorrow";
			} else {
				day = DayNames[PyWeekday(start)] + " " + start.Day.ToString(CultureInfo.InvariantCulture) + " " + MonthNames[start.Month - 1];
			}
			return day + ", " + SpokenTimes(start, end);
		}

		/// <summary>find_slots.display_window: "Monday 28 September 2026, 2:00 PM – 4:00 PM (Central Time)" (en dash).</summary>
		public static string DisplayWindow(DateTime start, DateTime end, string timezoneLabel) {
			Func<DateTime, string> clock = moment => {
				int hour = moment.Hour % 12 == 0 ? 12 : moment.Hour % 12;
				return hour.ToString(CultureInfo.InvariantCulture) + ":" + moment.Minute.ToString("00", CultureInfo.InvariantCulture)
					+ " " + (moment.Hour < 12 ? "AM" : "PM");
			};
			return DayNames[PyWeekday(start)] + " " + start.Day.ToString(CultureInfo.InvariantCulture) + " " + MonthNames[start.Month - 1]
				+ " " + start.Year.ToString(CultureInfo.InvariantCulture) + ", " + clock(start) + " – " + clock(end)
				+ " (" + timezoneLabel + ")";
		}

		private static int TargetDays(string priority, Cr8ServiceCalendarConfig config) {
			switch (priority) {
				case "High": return config.TargetDaysHigh;
				case "Medium": return config.TargetDaysMedium;
				default: return config.TargetDaysLow;
			}
		}

		/// <summary>
		/// C1 — port of find_slots.find_slots. All times are wall-clock in the calling user's timezone.
		/// A preference exists when <paramref name="preferredDate"/> or <paramref name="preferredStartTime"/> is given
		/// (Python: preference = {"date"?, "start"?}); missing date = today, missing start = 00:00.
		/// </summary>
		public static Cr8SlotOffer FindEarliestSlot(DateTime now, string priority, IList<Cr8TechnicianCandidate> technicians,
				IList<Cr8BusyPeriod> booked, DateTime? preferredDate, string preferredStartTime, Cr8ServiceCalendarConfig config) {
			string normalizedPriority = NormalizePriority(priority);
			if (normalizedPriority == null) {
				return new Cr8SlotOffer { Status = "error", Reason = "priority must be High, Medium or Low", PreferenceMatched = "none" };
			}
			technicians = technicians ?? new List<Cr8TechnicianCandidate>();
			var technicianIds = new HashSet<Guid>(technicians.Select(t => t.ContactId));
			var busy = new Dictionary<Guid, List<Cr8BusyPeriod>>();
			foreach (Cr8BusyPeriod booking in booked ?? new List<Cr8BusyPeriod>()) {
				if (!technicianIds.Contains(booking.OwnerId)) {
					continue;
				}
				List<Cr8BusyPeriod> list;
				if (!busy.TryGetValue(booking.OwnerId, out list)) {
					list = new List<Cr8BusyPeriod>();
					busy[booking.OwnerId] = list;
				}
				list.Add(booking);
			}
			DateTime today = now.Date;
			List<DateTime> days = ServiceDays(today, config.HorizonBusinessDays, config.Days);
			if (technicians.Count == 0) {
				return new Cr8SlotOffer { Status = "none", Reason = "no_qualified_technicians", WithinTarget = false, PreferenceMatched = "none" };
			}
			bool hasPreference = preferredDate.HasValue || !string.IsNullOrWhiteSpace(preferredStartTime);
			bool hasPreferredStart = !string.IsNullOrWhiteSpace(preferredStartTime);
			DateTime? preferredStart = null;
			if (hasPreference) {
				DateTime preferredDay = preferredDate.HasValue ? preferredDate.Value.Date : today;
				int prefHours, prefMinutes;
				ParseHhMm(hasPreferredStart ? preferredStartTime.Trim() : "00:00", "PreferredStartTime", out prefHours, out prefMinutes);
				preferredStart = preferredDay.AddHours(prefHours).AddMinutes(prefMinutes);
			}
			DateTime earliestAllowed = now.AddMinutes(config.LeadMinutes);
			TimeSpan windowLength = TimeSpan.FromHours(config.WindowHours);
			List<Cr8TechnicianCandidate> ordered = technicians
				.OrderBy(t => t.Name ?? string.Empty, StringComparer.Ordinal)
				.ThenBy(t => t.ContactId.ToString("D"), StringComparer.Ordinal)
				.ToList();

			DateTime foundStart = DateTime.MinValue, foundEnd = DateTime.MinValue;
			Cr8TechnicianCandidate foundTechnician = null;
			foreach (DateTime day in days) {
				foreach (string windowStart in config.WindowStarts) {
					int hours, minutes;
					ParseHhMm(windowStart, "Cr8VantageWindowStarts", out hours, out minutes);
					DateTime start = day.AddHours(hours).AddMinutes(minutes);
					DateTime end = start.Add(windowLength);
					if (start < earliestAllowed || (preferredStart.HasValue && start < preferredStart.Value)) {
						continue;
					}
					foreach (Cr8TechnicianCandidate technician in ordered) {
						List<Cr8BusyPeriod> periods;
						bool overlaps = busy.TryGetValue(technician.ContactId, out periods)
							&& periods.Any(b => Overlaps(b.Start, b.End, start, end));
						if (!overlaps) {
							foundStart = start;
							foundEnd = end;
							foundTechnician = technician;
							break;
						}
					}
					if (foundTechnician != null) {
						break;
					}
				}
				if (foundTechnician != null) {
					break;
				}
			}
			if (foundTechnician == null) {
				return new Cr8SlotOffer { Status = "none", Reason = "no_free_window_in_horizon", WithinTarget = false,
					PreferenceMatched = hasPreference ? "no" : "none" };
			}
			int distance = BusinessDaysBetween(today, foundStart.Date, config.Days);
			bool withinTarget = distance <= TargetDays(normalizedPriority, config);
			if (normalizedPriority == "High" && !withinTarget && !hasPreference) {
				withinTarget = true; // KB-03: High is "same day, or first available slot"
			}
			string matched;
			if (!hasPreference) {
				matched = "none";
			} else if (hasPreferredStart) {
				matched = foundStart == preferredStart.Value ? "yes" : "no";
			} else {
				matched = foundStart.Date == preferredStart.Value.Date ? "yes" : "no";
			}
			return new Cr8SlotOffer {
				Status = "ok",
				Reason = string.Empty,
				TechnicianContactId = foundTechnician.ContactId,
				TechnicianName = foundTechnician.Name,
				WindowStart = foundStart,
				WindowEnd = foundEnd,
				SpokenWindow = SpokenWindow(foundStart, foundEnd, today),
				DisplayWindow = DisplayWindow(foundStart, foundEnd, config.TimezoneLabel),
				WithinTarget = withinTarget,
				PreferenceMatched = matched,
				BusinessDaysFromToday = distance
			};
		}

		/// <summary>C2 — true when no busy period of the technician overlaps [start, end).</summary>
		public static bool IsWindowFree(IList<Cr8BusyPeriod> ownerBusy, DateTime start, DateTime end) {
			return !(ownerBusy ?? new List<Cr8BusyPeriod>()).Any(b => Overlaps(b.Start, b.End, start, end));
		}

		/// <summary>C3 — step forward one calendar day at a time, counting only service days, until <paramref name="days"/> are counted.</summary>
		public static DateTime AddBusinessDays(DateTime from, int days, IList<string> serviceDays) {
			DateTime current = from.Date;
			int counted = 0;
			while (counted < days) {
				current = current.AddDays(1);
				if (IsServiceDay(current, serviceDays)) {
					counted++;
				}
			}
			return current;
		}

		/// <summary>C6 (P2) — late change when the visit starts less than <paramref name="lateChangeHours"/> from now.</summary>
		public static bool IsLateChange(DateTime now, DateTime visitStart, int lateChangeHours) {
			return visitStart - now < TimeSpan.FromHours(lateChangeHours);
		}
	}

	/// <summary>Pure current-job rule: port of current_job.py (C5).</summary>
	public static class Cr8JobRules
	{
		/// <summary>In progress → most recently ended → next upcoming; the first non-empty group wins. Today only; no-case Activities ignored.</summary>
		public static Cr8CurrentJobPick PickCurrentJob(DateTime now, IList<Cr8CalendarActivity> activities) {
			var pick = new Cr8CurrentJobPick { Basis = "none" };
			var jobs = new List<Cr8CalendarActivity>();
			foreach (Cr8CalendarActivity activity in activities ?? new List<Cr8CalendarActivity>()) {
				if (string.IsNullOrEmpty(activity.CaseId)) {
					pick.IgnoredNoCase++;
					continue;
				}
				if (activity.Start.Date != now.Date) {
					pick.IgnoredNotToday++;
					continue;
				}
				jobs.Add(activity);
			}
			List<Cr8CalendarActivity> inProgress = jobs.Where(j => j.Start <= now && now < j.End)
				.OrderBy(j => j.Start).ThenBy(j => j.Id, StringComparer.Ordinal).ToList();
			List<Cr8CalendarActivity> mostRecent = jobs.Where(j => j.End <= now)
				.OrderByDescending(j => j.End).ThenBy(j => j.Id, StringComparer.Ordinal).ToList();
			List<Cr8CalendarActivity> nextUpcoming = jobs.Where(j => j.Start > now)
				.OrderBy(j => j.Start).ThenBy(j => j.Id, StringComparer.Ordinal).ToList();
			var groups = new[] {
				new KeyValuePair<string, List<Cr8CalendarActivity>>("in_progress", inProgress),
				new KeyValuePair<string, List<Cr8CalendarActivity>>("most_recent", mostRecent),
				new KeyValuePair<string, List<Cr8CalendarActivity>>("next_upcoming", nextUpcoming)
			};
			foreach (KeyValuePair<string, List<Cr8CalendarActivity>> group in groups) {
				if (group.Value.Count > 0) {
					pick.Basis = group.Key;
					pick.Job = group.Value[0];
					pick.OtherCandidates = group.Value.Count - 1;
					return pick;
				}
			}
			return pick;
		}

		/// <summary>Street = Cr8ServiceAddress up to the first comma (Part A §6.4 C5).</summary>
		public static string StreetFromAddress(string serviceAddress) {
			if (string.IsNullOrEmpty(serviceAddress)) {
				return string.Empty;
			}
			int comma = serviceAddress.IndexOf(',');
			return (comma < 0 ? serviceAddress : serviceAddress.Substring(0, comma)).Trim();
		}
	}

	/// <summary>C4 — phone normaliser (references/phone-normalisation.md, rules 1, 3, 4). Spoken words are turned into digits by the model before the call.</summary>
	public static class Cr8PhoneNormalizer
	{
		/// <summary>Digits only; 11 digits starting with 1 → drop the 1; 7 → NNN-NNNN; 10 → NNN-NNN-NNNN; any other length → "" (do not search).</summary>
		public static string Normalize(string raw) {
			var digits = new StringBuilder();
			foreach (char ch in raw ?? string.Empty) {
				if (ch >= '0' && ch <= '9') {
					digits.Append(ch);
				}
			}
			string value = digits.ToString();
			if (value.Length == 11 && value[0] == '1') {
				value = value.Substring(1);
			}
			if (value.Length == 7) {
				return value.Substring(0, 3) + "-" + value.Substring(3);
			}
			if (value.Length == 10) {
				return value.Substring(0, 3) + "-" + value.Substring(3, 3) + "-" + value.Substring(6);
			}
			return string.Empty;
		}
	}

	/// <summary>Timezone helpers that need no Terrasoft types (used by the tests and by Cr8VantageClock).</summary>
	public static class Cr8TimeRules
	{
		public static DateTime FromUtc(DateTime utc, TimeZoneInfo zone) {
			return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
		}

		public static DateTime ToUtc(DateTime wallClock, TimeZoneInfo zone) {
			return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified), zone);
		}
	}

	// ==== PURE-LOGIC END ====

	/// <summary>Current time in the calling user's timezone (the service user and all Vantage users are on Central, BP-3 / G17).</summary>
	public static class Cr8VantageClock
	{
		/// <summary>
		/// ESQ DateTime convention. false = ESQ returns and accepts wall-clock values in the current user's timezone
		/// (matches service-calendar.json "record_times_are": "instance_wall_clock").
		/// </summary>
		public const bool RecordTimesAreUtc = false; // [VERIFY: A0.3 probe — read Alex's 08:00 Activity StartDate through ESQ as the service user; 08:00 → keep false, 13:00 → set true]

		public static TimeZoneInfo UserZone(UserConnection userConnection) {
			return userConnection.CurrentUser.TimeZone; // [VERIFY: UserInfo.TimeZone property name/type on this version (A0.3)]
		}

		public static DateTime Now(UserConnection userConnection) {
			return Cr8TimeRules.FromUtc(DateTime.UtcNow, UserZone(userConnection));
		}

		/// <summary>A DateTime read through ESQ → wall clock.</summary>
		public static DateTime FromRecord(UserConnection userConnection, DateTime value) {
			return RecordTimesAreUtc ? Cr8TimeRules.FromUtc(value, UserZone(userConnection)) : value;
		}

		/// <summary>A wall-clock DateTime → the value to put in an ESQ filter.</summary>
		public static DateTime ToRecord(UserConnection userConnection, DateTime wallClock) {
			return RecordTimesAreUtc ? Cr8TimeRules.ToUtc(wallClock, UserZone(userConnection)) : wallClock;
		}
	}

	/// <summary>Reads the Cr8Vantage* system settings (Part A §2.2). Defaults equal the documented values.</summary>
	public static class Cr8VantageSettings
	{
		private static IList<string> SplitList(string value, IList<string> fallback) {
			if (string.IsNullOrWhiteSpace(value)) {
				return fallback;
			}
			return value.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
		}

		/// <summary>
		/// ActivityCategory "Visit" (setting Cr8VantageVisitCategory). Only Visit-category Activities block a technician's
		/// slots and count as jobs (BP, 24 Sep 2026), so rework, review and email Activities never do.
		/// Missing setting → throw; the user tasks turn it into Status/Reason = error instead of silently blocking on everything.
		/// </summary>
		public static Guid VisitCategoryId(UserConnection userConnection) {
			Guid id = SysSettings.GetValue(userConnection, "Cr8VantageVisitCategory", Guid.Empty);
			if (id == Guid.Empty) {
				throw new InvalidOperationException("visit_category_not_set: system setting Cr8VantageVisitCategory is empty");
			}
			return id;
		}

		/// <summary>ServicePact for Vantage cases (setting Cr8VantageServicePact). Empty → Guid.Empty and the case gets no SLA; never throws.</summary>
		public static Guid ServicePactId(UserConnection userConnection) {
			return SysSettings.GetValue(userConnection, "Cr8VantageServicePact", Guid.Empty);
		}

		/// <summary>ServiceItem for Vantage cases (setting Cr8VantageServiceItem). Empty → Guid.Empty and the case gets no SLA; never throws.</summary>
		public static Guid ServiceItemId(UserConnection userConnection) {
			return SysSettings.GetValue(userConnection, "Cr8VantageServiceItem", Guid.Empty);
		}

		public static Cr8ServiceCalendarConfig ReadCalendar(UserConnection userConnection) {
			var defaults = new Cr8ServiceCalendarConfig();
			return new Cr8ServiceCalendarConfig {
				Days = SplitList(SysSettings.GetValue(userConnection, "Cr8VantageServiceDays", string.Empty), defaults.Days),
				WindowStarts = SplitList(SysSettings.GetValue(userConnection, "Cr8VantageWindowStarts", string.Empty), defaults.WindowStarts),
				WindowHours = SysSettings.GetValue(userConnection, "Cr8VantageWindowHours", defaults.WindowHours),
				LeadMinutes = SysSettings.GetValue(userConnection, "Cr8VantageLeadMinutes", defaults.LeadMinutes),
				HorizonBusinessDays = SysSettings.GetValue(userConnection, "Cr8VantageHorizonBusinessDays", defaults.HorizonBusinessDays),
				TargetDaysHigh = SysSettings.GetValue(userConnection, "Cr8VantageTargetDaysHigh", defaults.TargetDaysHigh),
				TargetDaysMedium = SysSettings.GetValue(userConnection, "Cr8VantageTargetDaysMedium", defaults.TargetDaysMedium),
				TargetDaysLow = SysSettings.GetValue(userConnection, "Cr8VantageTargetDaysLow", defaults.TargetDaysLow),
				TimezoneLabel = SysSettings.GetValue(userConnection, "Cr8VantageTimezoneLabel", defaults.TimezoneLabel)
			};
		}
	}

	/// <summary>C1, C2, C6 — technician availability over ESQ.</summary>
	public class Cr8TechnicianAvailabilityService
	{
		/// <summary>Employees holding the EmployeeJob (exact name) → candidate technicians. Returns null when the job name is unknown.</summary>
		public List<Cr8TechnicianCandidate> ReadQualifiedTechnicians(UserConnection userConnection, string jobName) {
			// Employee.Job references EmployeeJob, not Job (verified on the trial, 25 Sep 2026).
			var jobEsq = new EntitySchemaQuery(userConnection.EntitySchemaManager, "EmployeeJob");
			jobEsq.PrimaryQueryColumn.IsAlwaysSelect = true;
			jobEsq.Filters.Add(jobEsq.CreateFilterWithParameters(FilterComparisonType.Equal, "Name", jobName ?? string.Empty));
			Entity job = jobEsq.GetEntityCollection(userConnection).FirstOrDefault();
			if (job == null) {
				return null;
			}
			var esq = new EntitySchemaQuery(userConnection.EntitySchemaManager, "Employee");
			EntitySchemaQueryColumn contactIdColumn = esq.AddColumn("Contact.Id");
			EntitySchemaQueryColumn contactNameColumn = esq.AddColumn("Contact.Name");
			esq.Filters.Add(esq.CreateFilterWithParameters(FilterComparisonType.Equal, "Job", job.PrimaryColumnValue));
			esq.Filters.Add(esq.CreateIsNotNullFilter("Contact"));
			var result = new List<Cr8TechnicianCandidate>();
			foreach (Entity row in esq.GetEntityCollection(userConnection)) {
				result.Add(new Cr8TechnicianCandidate {
					ContactId = row.GetTypedColumnValue<Guid>(contactIdColumn.Name),
					Name = row.GetTypedColumnValue<string>(contactNameColumn.Name)
				});
			}
			return result;
		}

		/// <summary>Visit-category Activities owned by the technician that end at or after <paramref name="fromWallClock"/> (the spec reads DueDate ≥ today).</summary>
		public List<Cr8BusyPeriod> ReadBusy(UserConnection userConnection, Guid technicianContactId, DateTime fromWallClock) {
			var esq = new EntitySchemaQuery(userConnection.EntitySchemaManager, "Activity");
			esq.AddColumn("StartDate");
			esq.AddColumn("DueDate");
			esq.Filters.Add(esq.CreateFilterWithParameters(FilterComparisonType.Equal, "Owner", technicianContactId));
			esq.Filters.Add(esq.CreateFilterWithParameters(FilterComparisonType.GreaterOrEqual, "DueDate",
				Cr8VantageClock.ToRecord(userConnection, fromWallClock)));
			// BP decision (24 Sep 2026): only Visit-category Activities block. This deliberately differs from find_slots.py,
			// where every Activity blocks, so a 24 h rework task can never hide a technician's free windows.
			esq.Filters.Add(esq.CreateFilterWithParameters(FilterComparisonType.Equal, "ActivityCategory",
				Cr8VantageSettings.VisitCategoryId(userConnection)));
			var result = new List<Cr8BusyPeriod>();
			foreach (Entity row in esq.GetEntityCollection(userConnection)) {
				result.Add(new Cr8BusyPeriod {
					OwnerId = technicianContactId,
					Start = Cr8VantageClock.FromRecord(userConnection, row.GetTypedColumnValue<DateTime>("StartDate")),
					End = Cr8VantageClock.FromRecord(userConnection, row.GetTypedColumnValue<DateTime>("DueDate"))
				});
			}
			return result;
		}

		/// <summary>C1 — earliest free window with a qualified technician (T2 vantage_find_technician_slot).</summary>
		public Cr8SlotOffer FindEarliestSlot(UserConnection userConnection, string jobName, string priority,
				DateTime? preferredDate, string preferredStartTime) {
			Cr8ServiceCalendarConfig config = Cr8VantageSettings.ReadCalendar(userConnection);
			DateTime now = Cr8VantageClock.Now(userConnection);
			List<Cr8TechnicianCandidate> technicians = ReadQualifiedTechnicians(userConnection, jobName);
			if (technicians == null) {
				return new Cr8SlotOffer { Status = "error", Reason = "unknown_job", PreferenceMatched = "none" };
			}
			var booked = new List<Cr8BusyPeriod>();
			foreach (Cr8TechnicianCandidate technician in technicians) {
				booked.AddRange(ReadBusy(userConnection, technician.ContactId, now.Date));
			}
			return Cr8SchedulingRules.FindEarliestSlot(now, priority, technicians, booked, preferredDate, preferredStartTime, config);
		}

		/// <summary>C2 — no overlapping Visit-category Activity for that owner (T3 window re-check).</summary>
		public bool IsWindowFree(UserConnection userConnection, Guid technicianContactId, DateTime start, DateTime end) {
			List<Cr8BusyPeriod> busy = ReadBusy(userConnection, technicianContactId, start.Date);
			return Cr8SchedulingRules.IsWindowFree(busy, start, end);
		}

		/// <summary>C6 (P2) — visitStart − now &lt; Cr8VantageLateChangeHours.</summary>
		public bool IsLateChange(UserConnection userConnection, DateTime visitStart) {
			int hours = SysSettings.GetValue(userConnection, "Cr8VantageLateChangeHours", 2);
			return Cr8SchedulingRules.IsLateChange(Cr8VantageClock.Now(userConnection), visitStart, hours);
		}
	}

	/// <summary>C3 — business calendar over Cr8VantageServiceDays.</summary>
	public class Cr8VantageBusinessCalendar
	{
		public DateTime AddBusinessDays(UserConnection userConnection, DateTime from, int days) {
			Cr8ServiceCalendarConfig config = Cr8VantageSettings.ReadCalendar(userConnection);
			return Cr8SchedulingRules.AddBusinessDays(from, days, config.Days);
		}
	}

	/// <summary>C5 — the job a technician is on now, just finished or has next (T5 vantage_get_technician_current_job).</summary>
	public class Cr8TechnicianJobService
	{
		private static readonly Guid ActivityStatusNotStarted = new Guid("384d4b84-58e6-df11-971b-001d60e938c6");

		/// <summary>True when the contact's user is a member of the Cr8VantageTechniciansRole role.</summary>
		public bool IsVantageTechnician(UserConnection userConnection, Guid technicianContactId) {
			Guid roleId = SysSettings.GetValue(userConnection, "Cr8VantageTechniciansRole", Guid.Empty);
			if (roleId == Guid.Empty || technicianContactId == Guid.Empty) {
				return false;
			}
			var userEsq = new EntitySchemaQuery(userConnection.EntitySchemaManager, "SysAdminUnit");
			userEsq.PrimaryQueryColumn.IsAlwaysSelect = true;
			userEsq.Filters.Add(userEsq.CreateFilterWithParameters(FilterComparisonType.Equal, "Contact", technicianContactId));
			foreach (Entity user in userEsq.GetEntityCollection(userConnection)) {
				var roleEsq = new EntitySchemaQuery(userConnection.EntitySchemaManager, "SysAdminUnitInRole");
				roleEsq.PrimaryQueryColumn.IsAlwaysSelect = true;
				roleEsq.Filters.Add(roleEsq.CreateFilterWithParameters(FilterComparisonType.Equal, "SysAdminUnit", user.PrimaryColumnValue));
				roleEsq.Filters.Add(roleEsq.CreateFilterWithParameters(FilterComparisonType.Equal, "SysAdminUnitRoleId", roleId)); // [VERIFY: SysAdminUnitInRole column names for ESQ (A0.3) — "SysAdminUnit" lookup and "SysAdminUnitRoleId"]
				if (roleEsq.GetEntityCollection(userConnection).Count > 0) {
					return true;
				}
			}
			return false;
		}

		public Cr8CurrentJob GetCurrentJob(UserConnection userConnection, Guid technicianContactId) {
			if (!IsVantageTechnician(userConnection, technicianContactId)) {
				return new Cr8CurrentJob { Basis = "not_a_technician" };
			}
			DateTime now = Cr8VantageClock.Now(userConnection);
			var esq = new EntitySchemaQuery(userConnection.EntitySchemaManager, "Activity");
			esq.PrimaryQueryColumn.IsAlwaysSelect = true;
			esq.AddColumn("StartDate");
			esq.AddColumn("DueDate");
			EntitySchemaQueryColumn caseColumn = esq.AddColumn("Case.Id");
			esq.Filters.Add(esq.CreateFilterWithParameters(FilterComparisonType.Equal, "Owner", technicianContactId));
			esq.Filters.Add(esq.CreateFilterWithParameters(FilterComparisonType.GreaterOrEqual, "StartDate",
				Cr8VantageClock.ToRecord(userConnection, now.Date)));
			esq.Filters.Add(esq.CreateFilterWithParameters(FilterComparisonType.Less, "StartDate",
				Cr8VantageClock.ToRecord(userConnection, now.Date.AddDays(1))));
			// Only visits count as jobs (same rule as C1/C2), so a rework task on the same case never becomes "the current job".
			esq.Filters.Add(esq.CreateFilterWithParameters(FilterComparisonType.Equal, "ActivityCategory",
				Cr8VantageSettings.VisitCategoryId(userConnection)));
			// Only visits not yet started count, so completed or cancelled visits never become "the current job" (BP, 28 Sep).
			esq.Filters.Add(esq.CreateFilterWithParameters(FilterComparisonType.Equal, "Status",
				ActivityStatusNotStarted));
			var activities = new List<Cr8CalendarActivity>();
			foreach (Entity row in esq.GetEntityCollection(userConnection)) {
				Guid caseId = row.GetTypedColumnValue<Guid>(caseColumn.Name);
				activities.Add(new Cr8CalendarActivity {
					Id = row.PrimaryColumnValue.ToString("D"),
					CaseId = caseId == Guid.Empty ? null : caseId.ToString("D"),
					Start = Cr8VantageClock.FromRecord(userConnection, row.GetTypedColumnValue<DateTime>("StartDate")),
					End = Cr8VantageClock.FromRecord(userConnection, row.GetTypedColumnValue<DateTime>("DueDate"))
				});
			}
			Cr8CurrentJobPick pick = Cr8JobRules.PickCurrentJob(now, activities);
			if (pick.Job == null) {
				return new Cr8CurrentJob { Basis = "none" };
			}
			var result = new Cr8CurrentJob {
				Basis = pick.Basis,
				CaseId = new Guid(pick.Job.CaseId),
				SpokenWindow = Cr8SchedulingRules.SpokenTimes(pick.Job.Start, pick.Job.End),
				OtherCandidates = pick.OtherCandidates
			};
			var caseEsq = new EntitySchemaQuery(userConnection.EntitySchemaManager, "Case");
			caseEsq.PrimaryQueryColumn.IsAlwaysSelect = true;
			caseEsq.AddColumn("Number");
			caseEsq.AddColumn("Subject");
			caseEsq.AddColumn("Solution");
			caseEsq.AddColumn("Cr8ServiceAddress");
			EntitySchemaQueryColumn feeColumn = caseEsq.AddColumn("Cr8FeePosition.Name");
			EntitySchemaQueryColumn equipmentColumn = caseEsq.AddColumn("ConfItem.Name");
			EntitySchemaQueryColumn givenNameColumn = caseEsq.AddColumn("Contact.GivenName");
			Entity caseEntity = caseEsq.GetEntity(userConnection, result.CaseId);
			if (caseEntity != null) {
				result.CaseNumber = caseEntity.GetTypedColumnValue<string>("Number");
				result.Subject = caseEntity.GetTypedColumnValue<string>("Subject");
				result.ExistingSolution = caseEntity.GetTypedColumnValue<string>("Solution");
				result.Street = Cr8JobRules.StreetFromAddress(caseEntity.GetTypedColumnValue<string>("Cr8ServiceAddress"));
				result.FeePosition = caseEntity.GetTypedColumnValue<string>(feeColumn.Name);
				result.EquipmentName = caseEntity.GetTypedColumnValue<string>(equipmentColumn.Name);
				result.CustomerGivenName = caseEntity.GetTypedColumnValue<string>(givenNameColumn.Name);
			}
			return result;
		}
	}
}
