namespace Terrasoft.Configuration.Cr8FieldServiceOps
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using global::Common.Logging;
	using Terrasoft.Core;
	using Terrasoft.Core.Configuration;
	using Terrasoft.Core.Entities;
	using Terrasoft.Core.Entities.Events;

	/// <summary>
	/// Applies the outcome of the native manager approval (SysApproval) raised by the
	/// Cr8VantageCaseApproval process on a Vantage-origin case.
	/// Positive: case moves to Resolved (the approver's connection passes Cr8VantageCaseGuardListener).
	/// Negative: the case moves to Rework and a high-priority rework task goes to the case owner.
	/// Never throws: failures are logged so the approval itself always saves.
	/// </summary>
	[EntityEventListener(SchemaName = "SysApproval")]
	public class Cr8VantageApprovalOutcome : BaseEntityEventListener
	{
		private static readonly ILog Log = LogManager.GetLogger("Cr8VantageApprovalOutcome");

		private static readonly Guid VisaStatusPositive = new Guid("e79facb3-3c32-43e7-a59e-12ba125e6132");
		private static readonly Guid VisaStatusNegative = new Guid("a93ab0b9-ca36-4b95-9b23-e01aa169c338");

		private static readonly Guid CaseStatusInProgress = new Guid("7e9f1204-f46b-1410-fb9a-0050ba5d6c38");
		private static readonly Guid CaseStatusResolved = new Guid("ae7f411e-f46b-1410-009b-0050ba5d6c38");

		private static readonly Guid ActivityTypeTask = new Guid("fbe0acdc-cfc0-df11-b00f-001d60e938c6");
		private static readonly Guid ActivityCategoryToDo = new Guid("f51c4643-58e6-df11-971b-001d60e938c6");
		private static readonly Guid ActivityStatusNotStarted = new Guid("384d4b84-58e6-df11-971b-001d60e938c6");
		private static readonly Guid ActivityPriorityHigh = new Guid("d625a9fc-7ee6-df11-971b-001d60e938c6");

		// Approvals whose status changed in OnSaving, consumed in OnSaved. Process-wide, not [ThreadStatic]
		// (same fix as Cr8VantageApprovalStarter).
		private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTime> StatusChanged =
			new System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTime>();

		public override void OnSaving(object sender, EntityBeforeEventArgs e) {
			base.OnSaving(sender, e);
			try {
				var entity = (Entity)sender;
				if (entity.GetChangedColumnValues().Any(column => column.Name == "StatusId")) {
					StatusChanged[entity.PrimaryColumnValue] = DateTime.UtcNow;
				}
			} catch (Exception ex) {
				Log.Error("Cr8VantageApprovalOutcome.OnSaving failed", ex);
			}
		}

		public override void OnSaved(object sender, EntityAfterEventArgs e) {
			base.OnSaved(sender, e);
			try {
				var approval = (Entity)sender;
				DateTime changedOn;
				if (!StatusChanged.TryRemove(approval.PrimaryColumnValue, out changedOn)) {
					return;
				}
				if (approval.GetTypedColumnValue<string>("ReferenceSchemaName") != "Case") {
					return;
				}
				Guid status = approval.GetTypedColumnValue<Guid>("StatusId");
				if (status != VisaStatusPositive && status != VisaStatusNegative) {
					return;
				}
				UserConnection userConnection = approval.UserConnection;
				Guid vantageOrigin = SysSettings.GetValue(userConnection, "Cr8VantageCaseOrigin", Guid.Empty);
				if (vantageOrigin == Guid.Empty) {
					return;
				}
				Guid caseId = approval.GetTypedColumnValue<Guid>("EntityId");
				EntitySchema caseSchema = userConnection.EntitySchemaManager.GetInstanceByName("Case");
				Entity caseEntity = caseSchema.CreateEntity(userConnection);
				if (!caseEntity.FetchFromDB(caseId)) {
					Log.WarnFormat("Cr8VantageApprovalOutcome: case {0} not found for approval {1}",
						caseId, approval.PrimaryColumnValue);
					return;
				}
				if (caseEntity.GetTypedColumnValue<Guid>("OriginId") != vantageOrigin) {
					return;
				}
				if (status == VisaStatusPositive) {
					ResolveCase(caseEntity);
				} else {
					CreateReworkTask(userConnection, caseEntity, approval.GetTypedColumnValue<string>("Comment"));
				}
			} catch (Exception ex) {
				Log.Error("Cr8VantageApprovalOutcome.OnSaved failed", ex);
			}
		}

		private static void ResolveCase(Entity caseEntity) {
			if (caseEntity.GetTypedColumnValue<Guid>("StatusId") == CaseStatusResolved) {
				return;
			}
			caseEntity.SetColumnValue("StatusId", CaseStatusResolved);
			caseEntity.Save(false);
		}

		private static void CreateReworkTask(UserConnection userConnection, Entity caseEntity, string comment) {
			Guid currentStatus = caseEntity.GetTypedColumnValue<Guid>("StatusId");
			if (currentStatus == Cr8VantageApprovalStarter.CaseStatusAwaitingApproval || currentStatus == CaseStatusInProgress) {
				caseEntity.SetColumnValue("StatusId", Cr8VantageApprovalStarter.CaseStatusRework);
				caseEntity.Save(false);
			} else {
				Log.InfoFormat("Cr8VantageApprovalOutcome: case {0} is not awaiting approval; status left as is, rework task still created",
					caseEntity.PrimaryColumnValue);
			}
			EntitySchema activitySchema = userConnection.EntitySchemaManager.GetInstanceByName("Activity");
			Entity activity = activitySchema.CreateEntity(userConnection);
			activity.SetDefColumnValues();
			DateTime now = userConnection.CurrentUser.GetCurrentDateTime();
			activity.SetColumnValue("Title", "Rework requested: " + caseEntity.GetTypedColumnValue<string>("Number"));
			Guid ownerId = caseEntity.GetTypedColumnValue<Guid>("OwnerId");
			if (ownerId != Guid.Empty) {
				activity.SetColumnValue("OwnerId", ownerId);
			}
			activity.SetColumnValue("CaseId", caseEntity.PrimaryColumnValue);
			activity.SetColumnValue("Notes", comment ?? string.Empty);
			activity.SetColumnValue("TypeId", ActivityTypeTask);
			activity.SetColumnValue("ActivityCategoryId", ActivityCategoryToDo);
			activity.SetColumnValue("StatusId", ActivityStatusNotStarted);
			activity.SetColumnValue("PriorityId", ActivityPriorityHigh);
			activity.SetColumnValue("StartDate", now);
			activity.SetColumnValue("DueDate", now.AddDays(1));
			activity.Save(false);
		}
	}
}
