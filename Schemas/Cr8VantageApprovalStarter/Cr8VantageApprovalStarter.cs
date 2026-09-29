namespace Terrasoft.Configuration.Cr8FieldServiceOps
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using global::Common.Logging;
	using Terrasoft.Core;
	using Terrasoft.Core.Configuration;
	using Terrasoft.Core.DB;
	using Terrasoft.Core.Entities;
	using Terrasoft.Core.Entities.Events;

	/// <summary>
	/// Deterministic start of the manager approval (process Cr8VantageCaseApproval) for Vantage-origin cases.
	/// Replaces the process's own record signal, which did not fire reliably.
	/// Fires on every ORM save of a Case (UI, OData, AI Studio creatio_update_record, C# actions) when:
	///   Solution changed to a non-empty value, Origin = Cr8VantageCaseOrigin, Status = In progress or Rework,
	///   and no open (not decided, not cancelled) SysApproval exists for the case.
	/// After the approval is created the case moves to Awaiting Manager Approval.
	/// Before the open-approval check, the case's open visit Activities are set to Completed
	/// (CompleteServiceVisits, kill switch Cr8VantageCompleteVisitOnSolution; BP, 27 Sep 2026).
	/// Never throws: a failed start is logged so the case save itself always succeeds.
	/// </summary>
	[EntityEventListener(SchemaName = "Case")]
	public class Cr8VantageApprovalStarter : BaseEntityEventListener
	{
		private static readonly ILog Log = LogManager.GetLogger("Cr8VantageApprovalStarter");

		private const string ProcessName = "Cr8VantageCaseApproval";
		private const string CaseIdParameter = "CaseId";

		private static readonly Guid CaseStatusInProgress = new Guid("7e9f1204-f46b-1410-fb9a-0050ba5d6c38");
		internal static readonly Guid CaseStatusAwaitingApproval = new Guid("2bcf2eb6-bc56-42fd-b2b4-397195f31aef");
		internal static readonly Guid CaseStatusRework = new Guid("a8e33b5d-915f-479d-8fa5-4aee520f2fbd");
		private static readonly Guid VisaStatusPositive = new Guid("e79facb3-3c32-43e7-a59e-12ba125e6132");
		private static readonly Guid VisaStatusNegative = new Guid("a93ab0b9-ca36-4b95-9b23-e01aa169c338");

		private const string TraceSetting = "Cr8VantageApprovalTrace";
		private const string CompleteVisitSetting = "Cr8VantageCompleteVisitOnSolution";

		// Cases whose Solution changed in OnSaving, consumed in OnSaved. Process-wide (not [ThreadStatic]):
		// OnSaved is not guaranteed to run on the OnSaving thread, which silently dropped every start.
		private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTime> SolutionChanged =
			new System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTime>();

		// Last decision, readable without server logs (system setting Cr8VantageApprovalTrace). Never throws.
		private static void Trace(UserConnection userConnection, Guid caseId, string message) {
			try {
				string line = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + "Z case " + caseId.ToString("D") + ": " + message;
				SysSettings.SetDefValue(userConnection, TraceSetting, line.Length > 490 ? line.Substring(0, 490) : line);
			} catch (Exception ex) {
				Log.Error("Cr8VantageApprovalStarter: trace write failed", ex);
			}
		}

		public override void OnSaving(object sender, EntityBeforeEventArgs e) {
			base.OnSaving(sender, e);
			try {
				var entity = (Entity)sender;
				if (!entity.GetChangedColumnValues().Any(column => column.Name == "Solution")) {
					return;
				}
				string newValue = entity.GetTypedColumnValue<string>("Solution");
				if (string.IsNullOrWhiteSpace(newValue)) {
					return;
				}
				string oldValue = entity.StoringState == StoringObjectState.New
					? null
					: entity.GetTypedOldColumnValue<string>("Solution");
				if (string.Equals(oldValue ?? string.Empty, newValue, StringComparison.Ordinal)) {
					return;
				}
				SolutionChanged[entity.PrimaryColumnValue] = DateTime.UtcNow;
			} catch (Exception ex) {
				Log.Error("Cr8VantageApprovalStarter.OnSaving failed", ex);
				Trace(((Entity)sender).UserConnection, ((Entity)sender).PrimaryColumnValue, "OnSaving error: " + ex.Message);
			}
		}

		public override void OnSaved(object sender, EntityAfterEventArgs e) {
			base.OnSaved(sender, e);
			var entity = (Entity)sender;
			Guid caseId = entity.PrimaryColumnValue;
			DateTime changedOn;
			UserConnection userConnection = entity.UserConnection;
			if (!SolutionChanged.TryRemove(caseId, out changedOn)) {
				try {
					Guid origin = SysSettings.GetValue(userConnection, "Cr8VantageCaseOrigin", Guid.Empty);
					if (origin != Guid.Empty && entity.GetTypedColumnValue<Guid>("OriginId") == origin) {
						Trace(userConnection, caseId, "saved; no Solution change detected");
					}
				} catch (Exception) {
					// diagnostics only
				}
				return;
			}
			// Appended to every trace written after the visit step (Trace overwrites one setting, so it is called once).
			string visitNote = string.Empty;
			try {
				Guid vantageOrigin = SysSettings.GetValue(userConnection, "Cr8VantageCaseOrigin", Guid.Empty);
				if (vantageOrigin == Guid.Empty || entity.GetTypedColumnValue<Guid>("OriginId") != vantageOrigin) {
					return;
				}
				Guid statusId = entity.GetTypedColumnValue<Guid>("StatusId");
				if (statusId != CaseStatusInProgress && statusId != CaseStatusRework) {
					Trace(userConnection, caseId, "skipped: status is not In progress or Rework");
					return;
				}
				// Own try/catch: a visit failure never stops the approval start, and an open approval never stops the visit.
				try {
					int completed = CompleteServiceVisits(userConnection, caseId);
					visitNote = completed < 0 ? "; visits: completion off" : "; visits completed: " + completed;
				} catch (Exception ex) {
					Log.Error("Cr8VantageApprovalStarter: failed to complete visits for case " + caseId, ex);
					visitNote = "; visits error: " + ex.GetType().Name + ": " + ex.Message;
				}
				if (HasOpenApproval(userConnection, caseId)) {
					Trace(userConnection, caseId, "skipped: an open approval already exists" + visitNote);
					return;
				}
				userConnection.ProcessEngine.ProcessExecutor.Execute(ProcessName,
					new Dictionary<string, string> { [CaseIdParameter] = caseId.ToString("D") });
				if (!HasOpenApproval(userConnection, caseId)) {
					Trace(userConnection, caseId, "started, but no open approval found afterwards" + visitNote);
					return;
				}
				// Pending approval is visible on the case: status Awaiting Manager Approval.
				// The re-save changes only StatusId, so this listener does not start another approval.
				Entity statusUpdate = userConnection.EntitySchemaManager.GetInstanceByName("Case").CreateEntity(userConnection);
				if (statusUpdate.FetchFromDB(caseId)) {
					statusUpdate.SetColumnValue("StatusId", CaseStatusAwaitingApproval);
					statusUpdate.Save(false);
				}
				Trace(userConnection, caseId, "started: approval created; status Awaiting Manager Approval" + visitNote);
			} catch (Exception ex) {
				Log.Error("Cr8VantageApprovalStarter: failed to start " + ProcessName + " for case " + caseId, ex);
				Trace(userConnection, caseId, "error: " + ex.GetType().Name + ": " + ex.Message
					+ (ex.InnerException != null ? " | inner: " + ex.InnerException.Message : string.Empty) + visitNote);
			}
		}

		/// <summary>
		/// Sets the case's open visit Activities (Visit category, title "Service visit…", status not finished) to Completed.
		/// Dates are left as booked, so the visit still blocks its window and still counts as the technician's job (C1, C2, C5).
		/// Never saves the Case. Returns the number completed, or -1 when setting Cr8VantageCompleteVisitOnSolution is off
		/// or missing (missing = off, same as Cr8VantageGuardEnabled). Internal so Cr8VantageApprovalOutcome can call it
		/// on approve instead, with a one-line move. May throw (e.g. Cr8VantageVisitCategory empty); the caller catches.
		/// </summary>
		internal static int CompleteServiceVisits(UserConnection userConnection, Guid caseId) {
			if (!SysSettings.GetValue(userConnection, CompleteVisitSetting, false)) {
				return -1;
			}
			var esq = new EntitySchemaQuery(userConnection.EntitySchemaManager, "Activity");
			// Record rights not applied: the saver may be the integration user, a technician or a manager.
			esq.UseAdminRights = false;
			esq.PrimaryQueryColumn.IsAlwaysSelect = true;
			esq.Filters.Add(esq.CreateFilterWithParameters(FilterComparisonType.Equal, "Case", caseId));
			esq.Filters.Add(esq.CreateFilterWithParameters(FilterComparisonType.Equal, "ActivityCategory",
				Cr8VantageSettings.VisitCategoryId(userConnection)));
			esq.Filters.Add(esq.CreateFilterWithParameters(FilterComparisonType.StartWith, "Title", Cr8Vantage.VisitTitlePrefix));
			esq.Filters.Add(esq.CreateFilterWithParameters(FilterComparisonType.Equal, "Status.Finish", false));
			List<Guid> visitIds = esq.GetEntityCollection(userConnection).Select(row => row.PrimaryColumnValue).ToList();
			EntitySchema activitySchema = userConnection.EntitySchemaManager.GetInstanceByName("Activity");
			int completed = 0;
			foreach (Guid visitId in visitIds) {
				Entity visit = activitySchema.CreateEntity(userConnection);
				if (!visit.FetchFromDB(visitId)) {
					continue;
				}
				visit.SetColumnValue("StatusId", Cr8Vantage.ActivityStatusCompleted);
				visit.Save(false);
				completed++;
			}
			return completed;
		}

		private static bool HasOpenApproval(UserConnection userConnection, Guid caseId) {
			var select = (Select)new Select(userConnection)
				.Column("StatusId")
				.From("SysApproval")
				.Where("EntityId").IsEqual(Column.Parameter(caseId))
				.And("ReferenceSchemaName").IsEqual(Column.Parameter("Case"))
				.And("IsCanceled").IsEqual(Column.Parameter(false));
			using (DBExecutor dbExecutor = userConnection.EnsureDBConnection()) {
				using (System.Data.IDataReader reader = select.ExecuteReader(dbExecutor)) {
					while (reader.Read()) {
						Guid status = reader.IsDBNull(0) ? Guid.Empty : reader.GetGuid(0);
						if (status != VisaStatusPositive && status != VisaStatusNegative) {
							return true;
						}
					}
				}
			}
			return false;
		}
	}
}
