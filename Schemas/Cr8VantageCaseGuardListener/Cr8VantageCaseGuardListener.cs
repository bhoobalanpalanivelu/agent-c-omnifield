namespace Terrasoft.Configuration.Cr8FieldServiceOps
{
	using System;
	using System.Linq;
	using Terrasoft.Core;
	using Terrasoft.Core.Configuration;
	using Terrasoft.Core.DB;
	using Terrasoft.Core.Entities;
	using Terrasoft.Core.Entities.Events;

	/// <summary>
	/// Guards status transitions on Vantage-origin cases.
	/// R1: the AI integration user and technicians never resolve or close a case.
	/// R2: In progress requires an owner and a visit Activity owned by that owner.
	/// </summary>
	[EntityEventListener(SchemaName = "Case")]
	public class Cr8VantageCaseGuardListener : BaseEntityEventListener
	{
		private static readonly Guid StatusInProgress = new Guid("7e9f1204-f46b-1410-fb9a-0050ba5d6c38");
		private static readonly Guid StatusResolved = new Guid("ae7f411e-f46b-1410-009b-0050ba5d6c38");
		private static readonly Guid StatusClosed = new Guid("3e7f420c-f46b-1410-fc9a-0050ba5d6c38");

		public override void OnSaving(object sender, EntityBeforeEventArgs e) {
			base.OnSaving(sender, e);
			var entity = (Entity)sender;
			UserConnection userConnection = entity.UserConnection;
			if (!SysSettings.GetValue(userConnection, "Cr8VantageGuardEnabled", false)) {
				return;
			}
			if (!entity.GetChangedColumnValues().Any(column => column.Name == "StatusId")) {
				return;
			}
			Guid vantageOrigin = SysSettings.GetValue(userConnection, "Cr8VantageCaseOrigin", Guid.Empty);
			if (vantageOrigin == Guid.Empty || entity.GetTypedColumnValue<Guid>("OriginId") != vantageOrigin) {
				return;
			}
			Guid newStatus = entity.GetTypedColumnValue<Guid>("StatusId");
			Guid oldStatus = entity.StoringState == StoringObjectState.New
				? Guid.Empty
				: entity.GetTypedOldColumnValue<Guid>("StatusId");
			bool isFinalStatus = newStatus == StatusResolved
				|| (newStatus == StatusClosed && oldStatus != StatusResolved);
			if (isFinalStatus && IsRestrictedUser(userConnection)) {
				throw new InvalidOperationException(
					"Cr8VantageCaseGuard: Vantage cases are resolved only through manager approval. " +
					"Record the solution; the case has been sent for approval.");
			}
			if (newStatus == StatusInProgress) {
				Guid ownerId = entity.GetTypedColumnValue<Guid>("OwnerId");
				if (ownerId == Guid.Empty || !VisitExists(userConnection, entity.PrimaryColumnValue, ownerId)) {
					throw new InvalidOperationException(
						"Cr8VantageCaseGuard: set Owner and create the visit Activity (Case = this case, " +
						"Owner = the technician) before moving the case to In progress. The booking is not complete.");
				}
			}
		}

		private static bool IsRestrictedUser(UserConnection userConnection) {
			Guid currentUserId = userConnection.CurrentUser.Id;
			Guid integrationUserId = SysSettings.GetValue(userConnection, "Cr8VantageIntegrationUser", Guid.Empty);
			if (integrationUserId != Guid.Empty && currentUserId == integrationUserId) {
				return true;
			}
			Guid techniciansRoleId = SysSettings.GetValue(userConnection, "Cr8VantageTechniciansRole", Guid.Empty);
			return techniciansRoleId != Guid.Empty && IsInRole(userConnection, currentUserId, techniciansRoleId);
		}

		private static bool IsInRole(UserConnection userConnection, Guid userId, Guid roleId) {
			var select = (Select)new Select(userConnection)
				.Column(Func.Count("Id"))
				.From("SysAdminUnitInRole")
				.Where("SysAdminUnitId").IsEqual(Column.Parameter(userId))
				.And("SysAdminUnitRoleId").IsEqual(Column.Parameter(roleId));
			return select.ExecuteScalar<int>() > 0;
		}

		private static bool VisitExists(UserConnection userConnection, Guid caseId, Guid ownerId) {
			var select = (Select)new Select(userConnection)
				.Column(Func.Count("Id"))
				.From("Activity")
				.Where("CaseId").IsEqual(Column.Parameter(caseId))
				.And("OwnerId").IsEqual(Column.Parameter(ownerId));
			return select.ExecuteScalar<int>() > 0;
		}
	}
}
