namespace Terrasoft.Configuration.Cr8FieldServiceOps
{
	using System;
	using System.Collections.Generic;
	using System.Globalization;
	using System.Linq;
	using System.Reflection;
	using Creatio.Copilot.Actions;
	using Newtonsoft.Json;
	using Terrasoft.Common;
	using Terrasoft.Core;
	using Terrasoft.Core.Configuration;
	using Terrasoft.Core.Entities;
	using Terrasoft.Core.Factories;

	// ======================================================================================
	// Vantage Field Service — MCP tools as C# source-code actions (Plan A, BP 24 Sep 2026).
	// Source code schema: Cr8VantageMcpActions (package Cr8FieldServiceOps).
	// Each class is published as one McpTool (ToolSourceType = source-code action,
	// SourceCodeAction = "Terrasoft.Configuration.Cr8FieldServiceOps.<Class>, Cr8FieldServiceOps").
	// Inputs arrive as strings; outputs are one JSON object matching the declared Output parameters.
	// A tool never throws to the model: failures come back as Status/Reason = "error".
	// Date-times in and out are wall clock in the calling user's timezone (Central), "yyyy-MM-ddTHH:mm:ss".
	// ======================================================================================

	/// <summary>Shared ids, lookups and helpers for the Vantage actions.</summary>
	internal static class Cr8Vantage
	{
		public static readonly Guid StatusNew = new Guid("ae5f2f10-f46b-1410-fd9a-0050ba5d6c38");
		public static readonly Guid StatusInProgress = new Guid("7e9f1204-f46b-1410-fb9a-0050ba5d6c38");
		public static readonly Guid CategoryServiceRequest = new Guid("1c0bc159-150a-e111-a31b-00155d04c01d");
		public static readonly Guid ActivityTypeTask = new Guid("fbe0acdc-cfc0-df11-b00f-001d60e938c6");
		public static readonly Guid ActivityTypeEmail = new Guid("e2831dec-cfc0-df11-b00f-001d60e938c6");
		public static readonly Guid ActivityCategoryToDo = new Guid("f51c4643-58e6-df11-971b-001d60e938c6");
		public static readonly Guid ActivityCategoryEmail = new Guid("8038a396-7825-e011-8165-00155d043204");
		public static readonly Guid ActivityStatusNotStarted = new Guid("384d4b84-58e6-df11-971b-001d60e938c6");
		public static readonly Guid ActivityStatusCompleted = new Guid("4bdbb88f-58e6-df11-971b-001d60e938c6");
		public static readonly Guid KbTypeRulesAndRegulations = new Guid("807f4276-f36b-1410-a29d-001d60e938c6");
		public static readonly Guid KbStatusDraft = new Guid("79a2f41d-4a2a-4b31-9e8a-9c91eb91a743");
		public const string DateTimeFormat = "yyyy-MM-ddTHH:mm:ss";
		public const string ConfirmationTitlePrefix = "Your Vantage visit is booked";
		// Visit Activity title prefix; Cr8VantageApprovalStarter.CompleteServiceVisits matches on it.
		public const string VisitTitlePrefix = "Service visit";
		public const string SenderAddress = "mayadispatcher@gmail.com";

		public static string Get(ActionExecutionOptions options, string name) {
			string value;
			return options.ParameterValues != null && options.ParameterValues.TryGetValue(name, out value)
				? (value ?? string.Empty).Trim()
				: string.Empty;
		}

		public static Guid GetGuid(ActionExecutionOptions options, string name) {
			Guid value;
			return Guid.TryParse(Get(options, name), out value) ? value : Guid.Empty;
		}

		public static DateTime? GetDateTime(ActionExecutionOptions options, string name) {
			string raw = Get(options, name);
			DateTime value;
			if (DateTime.TryParseExact(raw, new[] { DateTimeFormat, "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm" },
					CultureInfo.InvariantCulture, DateTimeStyles.None, out value)) {
				return value;
			}
			return null;
		}

		public static string Format(DateTime value) {
			return value == DateTime.MinValue ? string.Empty : value.ToString(DateTimeFormat, CultureInfo.InvariantCulture);
		}

		public static SourceCodeActionParameter In(string name, string description, bool required = false) {
			return new SourceCodeActionParameter {
				Name = name,
				Caption = new LocalizableString(name),
				Description = new LocalizableString(description),
				IsRequired = required,
				DataValueTypeUId = DataValueType.MediumTextDataValueTypeUId
			};
		}

		public static SourceCodeActionParameter Out(string name, string description, Guid? dataValueTypeUId = null) {
			return new SourceCodeActionParameter {
				Name = name,
				Caption = new LocalizableString(name),
				Description = new LocalizableString(description),
				Direction = ParameterDirection.Output,
				DataValueTypeUId = dataValueTypeUId ?? DataValueType.MediumTextDataValueTypeUId
			};
		}

		public static CopilotActionExecutionResult Result(Dictionary<string, object> output) {
			return new CopilotActionExecutionResult {
				Status = CopilotActionExecutionStatus.Completed,
				Response = JsonConvert.SerializeObject(output)
			};
		}

		public static Guid FindIdByName(UserConnection userConnection, string schemaName, string name) {
			if (string.IsNullOrWhiteSpace(name)) {
				return Guid.Empty;
			}
			var esq = new EntitySchemaQuery(userConnection.EntitySchemaManager, schemaName);
			esq.PrimaryQueryColumn.IsAlwaysSelect = true;
			esq.Filters.Add(esq.CreateFilterWithParameters(FilterComparisonType.Equal, "Name", name.Trim()));
			Entity row = esq.GetEntityCollection(userConnection).FirstOrDefault();
			return row == null ? Guid.Empty : row.PrimaryColumnValue;
		}

		public static Entity NewEntity(UserConnection userConnection, string schemaName) {
			Entity entity = userConnection.EntitySchemaManager.GetInstanceByName(schemaName).CreateEntity(userConnection);
			entity.SetDefColumnValues();
			return entity;
		}

		/// <summary>Sets the Vantage SLA and Service on a new, unsaved case and fills ResponseDate / SolutionDate. No-op when either setting is empty.</summary>
		public static void ApplySla(UserConnection userConnection, Entity caseEntity) {
			Guid servicePactId = Cr8VantageSettings.ServicePactId(userConnection);
			Guid serviceItemId = Cr8VantageSettings.ServiceItemId(userConnection);
			if (servicePactId == Guid.Empty || serviceItemId == Guid.Empty) {
				return;
			}
			caseEntity.SetColumnValue("ServicePactId", servicePactId);
			caseEntity.SetColumnValue("ServiceItemId", serviceItemId);
			// Explicit call: the OOTB on-save recalculation sits behind feature CalculateTermOnCaseEntity,
			// and a code-created case never gets SLA/Service from the page. Calculate never throws.
			// CaseTermCalculationManager (CrtSLM) lives in the configuration assembly, which this assembly
			// package cannot reference at compile time (same trap as Cr8VantageMail), so bind it at run time.
			Type managerType = Cr8VantageMail.FindType(
				"Terrasoft.Configuration.CaseTermCalculationManager, Terrasoft.Configuration",
				"Terrasoft.Configuration.CaseTermCalculationManager");
			MethodInfo calculate = managerType == null ? null : managerType.GetMethod("Calculate", new[] { typeof(Entity) });
			if (calculate == null) {
				return;
			}
			try {
				calculate.Invoke(Activator.CreateInstance(managerType, userConnection), new object[] { caseEntity });
			} catch (Exception) {
				// SLA must never fail a booking; the case keeps its SLA/Service and gets no deadlines
			}
		}

		public static void TryDelete(UserConnection userConnection, string schemaName, Guid id) {
			if (id == Guid.Empty) {
				return;
			}
			try {
				Entity entity = userConnection.EntitySchemaManager.GetInstanceByName(schemaName).CreateEntity(userConnection);
				if (entity.FetchFromDB(id)) {
					entity.Delete();
				}
			} catch (Exception) {
				// best effort compensation; the partial record stays Status New and is visible in the Vantage list
			}
		}
	}

	/// <summary>
	/// Late-bound email send. EmailClientFactory / ActivityEmailSenderBase live in CrtEmailSender and
	/// IEmailClientFactory in IntegrationApi, which are not dependencies of this assembly package, so a
	/// compile-time reference breaks the package build. Resolve the types at run time instead.
	/// </summary>
	internal static class Cr8VantageMail
	{
		internal static Type FindType(string assemblyQualifiedName, string fullName) {
			Type type = null;
			try {
				type = Type.GetType(assemblyQualifiedName, false);
			} catch (Exception) {
				type = null;
			}
			if (type != null) {
				return type;
			}
			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) {
				try {
					type = assembly.GetType(fullName, false);
				} catch (Exception) {
					type = null;
				}
				if (type != null) {
					return type;
				}
			}
			return null;
		}

		public static void SendActivityEmail(UserConnection userConnection, Guid activityId) {
			Type factoryType = FindType("Terrasoft.Mail.Sender.EmailClientFactory, CrtEmailSender",
				"Terrasoft.Mail.Sender.EmailClientFactory");
			Type senderType = FindType("Terrasoft.Mail.Sender.ActivityEmailSender, Terrasoft.Configuration",
				"Terrasoft.Mail.Sender.ActivityEmailSender");
			if (factoryType == null || senderType == null) {
				throw new InvalidOperationException("email sender types are not available");
			}
			MethodInfo getMethod = typeof(ClassFactory).GetMethods(BindingFlags.Public | BindingFlags.Static)
				.First(m => m.Name == "Get" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1
					&& m.GetParameters()[0].ParameterType == typeof(ConstructorArgument[]));
			try {
				object factory = getMethod.MakeGenericMethod(factoryType).Invoke(null,
					new object[] { new[] { new ConstructorArgument("userConnection", userConnection) } });
				object sender = Activator.CreateInstance(senderType, factory, userConnection);
				MethodInfo send = senderType.GetMethod("Send",
						BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, null,
						new[] { typeof(Guid) }, null)
					?? senderType.GetMethod("Send", new[] { typeof(Guid) });
				if (send == null) {
					throw new InvalidOperationException("ActivityEmailSender.Send(Guid) not found");
				}
				send.Invoke(sender, new object[] { activityId });
			} catch (TargetInvocationException ex) when (ex.InnerException != null) {
				throw ex.InnerException;
			}
		}
	}

	/// <summary>Base for Vantage actions: user connection wiring and the never-throw envelope.</summary>
	public abstract class Cr8VantageActionBase : BaseExecutableCodeAction, IUserConnectionRequired
	{
		protected UserConnection UserConnection { get; private set; }

		public void SetUserConnection(UserConnection userConnection) {
			UserConnection = userConnection;
		}

		protected override bool GetIsEnabled() {
			return true;
		}

		protected UserConnection Connection {
			get { return UserConnection ?? ClassFactory.Get<UserConnection>(); }
		}

		public override CopilotActionExecutionResult Execute(ActionExecutionOptions options) {
			try {
				return Cr8Vantage.Result(Run(options));
			} catch (Exception ex) {
				var error = new Dictionary<string, object>();
				FillError(error, ex.Message);
				return Cr8Vantage.Result(error);
			}
		}

		protected abstract Dictionary<string, object> Run(ActionExecutionOptions options);

		protected virtual void FillError(Dictionary<string, object> output, string message) {
			output["Status"] = "error";
			output["Reason"] = "error: " + message;
		}
	}

	// ---- T1 vantage_get_customer_context -------------------------------------------------------

	public class Cr8GetCustomerContextAction : Cr8VantageActionBase
	{
		// CommunicationType rows that hold a callable contact number (DisplayFormat "phone"; Fax and Extension phone excluded).
		private static readonly Guid CommunicationTypeMobilePhone = new Guid("d4a2dc80-30ca-df11-9b2a-001d60e938c6");
		private static readonly Guid CommunicationTypeBusinessPhone = new Guid("3dddb3cc-53ee-49c4-a71f-e9e257f59e49");
		private static readonly Guid CommunicationTypeHomePhone = new Guid("0da6a26b-d7bc-df11-b00f-001d60e938c6");
		private static readonly Guid CommunicationTypeOtherPhone = new Guid("21c0d693-9a52-43fa-b7f1-c6d8b53975d4");

		public Cr8GetCustomerContextAction() {
			Parameters = new List<SourceCodeActionParameter> {
				Cr8Vantage.In("PhoneNumber", "Customer phone number as digits, e.g. 555-0199.", true),
				Cr8Vantage.In("StreetHint", "Optional street to disambiguate several matching accounts."),
				Cr8Vantage.In("EquipmentHint", "Optional equipment name fragment when the customer owns several units."),
				Cr8Vantage.Out("Status", "ok | not_found | multiple | error"),
				Cr8Vantage.Out("ContactMatchCount", "Number of contacts matching the phone", DataValueType.IntegerDataValueTypeUId),
				Cr8Vantage.Out("ContactId", "Contact id when exactly one matched"),
				Cr8Vantage.Out("ContactGivenName", "Customer first name"),
				Cr8Vantage.Out("RegisteredAddress", "Primary address: street, city"),
				Cr8Vantage.Out("EquipmentCount", "Number of equipment records matched", DataValueType.IntegerDataValueTypeUId),
				Cr8Vantage.Out("ConfItemId", "Equipment id"),
				Cr8Vantage.Out("ConfItemName", "Equipment name"),
				Cr8Vantage.Out("WarrantyUntil", "Warranty end date yyyy-MM-dd"),
				Cr8Vantage.Out("WarrantyStatus", "InWarranty | Expired | Unknown"),
				Cr8Vantage.Out("OpenCaseId", "Open Vantage case in progress, if any"),
				Cr8Vantage.Out("OpenCaseNumber", "Its number"),
				Cr8Vantage.Out("Reason", "Error detail")
			}.AsReadOnly();
		}

		public override LocalizableString GetCaption() {
			return new LocalizableString("Vantage: get customer context");
		}

		public override LocalizableString GetDescription() {
			return new LocalizableString("Find a Vantage customer by phone number and return their registered address, equipment and the warranty verdict. Call before discussing fees or booking. Do not use to find a technician.");
		}

		protected override Dictionary<string, object> Run(ActionExecutionOptions options) {
			UserConnection uc = Connection;
			var output = new Dictionary<string, object> { ["Status"] = "ok", ["Reason"] = string.Empty,
				["ContactMatchCount"] = 0, ["EquipmentCount"] = 0 };
			string digits = new string(Cr8Vantage.Get(options, "PhoneNumber").Where(ch => ch >= '0' && ch <= '9').ToArray());
			// A leading 1 before a 7- or 10-digit number is the country code (+1 555 0199, 1-555-555-0199).
			if ((digits.Length == 8 || digits.Length == 11) && digits[0] == '1') {
				digits = digits.Substring(1);
			}
			if (digits.Length < 7) {
				output["Status"] = "not_found";
				output["Reason"] = "phone number not recognised";
				return output;
			}
			// ContactCommunication.SearchNumber holds the digits of Number reversed (kept by the platform), so
			// StartWith on the reversed input matches a stored number in any format that ends with these digits.
			string reversedDigits = new string(digits.Reverse().ToArray());
			string streetHint = Cr8Vantage.Get(options, "StreetHint");
			var communicationEsq = new EntitySchemaQuery(uc.EntitySchemaManager, "ContactCommunication");
			EntitySchemaQueryColumn contactIdColumn = communicationEsq.AddColumn("Contact.Id");
			communicationEsq.Filters.Add(communicationEsq.CreateFilterWithParameters(FilterComparisonType.StartWith,
				"SearchNumber", reversedDigits));
			communicationEsq.Filters.Add(communicationEsq.CreateFilterWithParameters(FilterComparisonType.Equal,
				"CommunicationType", CommunicationTypeMobilePhone, CommunicationTypeBusinessPhone,
				CommunicationTypeHomePhone, CommunicationTypeOtherPhone));
			List<Guid> contactIds = communicationEsq.GetEntityCollection(uc)
				.Select(row => row.GetTypedColumnValue<Guid>(contactIdColumn.Name))
				.Where(id => id != Guid.Empty).Distinct().ToList();
			var contacts = new List<Entity>();
			if (contactIds.Count > 0) {
				var contactEsq = new EntitySchemaQuery(uc.EntitySchemaManager, "Contact");
				contactEsq.PrimaryQueryColumn.IsAlwaysSelect = true;
				contactEsq.AddColumn("GivenName");
				contactEsq.Filters.Add(contactEsq.CreateFilterWithParameters(FilterComparisonType.Equal, "Id",
					contactIds.Cast<object>().ToArray()));
				contacts = contactEsq.GetEntityCollection(uc).ToList();
			}
			if (contacts.Count > 1 && streetHint.Length > 0) {
				contacts = contacts.Where(c => PrimaryAddress(uc, c.PrimaryColumnValue)
					.IndexOf(streetHint, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
			}
			output["ContactMatchCount"] = contacts.Count;
			if (contacts.Count != 1) {
				output["Status"] = contacts.Count == 0 ? "not_found" : "multiple";
				return output;
			}
			Entity contact = contacts[0];
			Guid contactId = contact.PrimaryColumnValue;
			output["ContactId"] = contactId.ToString("D");
			output["ContactGivenName"] = contact.GetTypedColumnValue<string>("GivenName");
			output["RegisteredAddress"] = PrimaryAddress(uc, contactId);

			string equipmentHint = Cr8Vantage.Get(options, "EquipmentHint");
			var itemEsq = new EntitySchemaQuery(uc.EntitySchemaManager, "ConfItem");
			itemEsq.PrimaryQueryColumn.IsAlwaysSelect = true;
			EntitySchemaQueryColumn nameColumn = itemEsq.AddColumn("Name");
			nameColumn.OrderByAsc();
			itemEsq.AddColumn("WarrantyUntil");
			itemEsq.Filters.Add(itemEsq.CreateFilterWithParameters(FilterComparisonType.Equal, "Owner", contactId));
			if (equipmentHint.Length > 0) {
				itemEsq.Filters.Add(itemEsq.CreateFilterWithParameters(FilterComparisonType.Contain, "Name", equipmentHint));
			}
			List<Entity> items = itemEsq.GetEntityCollection(uc).ToList();
			output["EquipmentCount"] = items.Count;
			output["WarrantyStatus"] = "Unknown";
			if (items.Count > 0) {
				Entity item = items[0];
				output["ConfItemId"] = item.PrimaryColumnValue.ToString("D");
				output["ConfItemName"] = item.GetTypedColumnValue<string>("Name");
				DateTime until = item.GetTypedColumnValue<DateTime>("WarrantyUntil");
				if (until != DateTime.MinValue) {
					output["WarrantyUntil"] = until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
					output["WarrantyStatus"] = until.Date >= Cr8VantageClock.Now(uc).Date ? "InWarranty" : "Expired";
				}
			}

			var caseEsq = new EntitySchemaQuery(uc.EntitySchemaManager, "Case");
			caseEsq.PrimaryQueryColumn.IsAlwaysSelect = true;
			caseEsq.AddColumn("Number");
			caseEsq.AddColumn("CreatedOn").OrderByDesc();
			caseEsq.RowCount = 1;
			caseEsq.Filters.Add(caseEsq.CreateFilterWithParameters(FilterComparisonType.Equal, "Contact", contactId));
			caseEsq.Filters.Add(caseEsq.CreateFilterWithParameters(FilterComparisonType.Equal, "Origin",
				SysSettings.GetValue(uc, "Cr8VantageCaseOrigin", Guid.Empty)));
			// Open = In progress, Awaiting Manager Approval or Rework (the approval stages keep the job open).
			caseEsq.Filters.Add(caseEsq.CreateFilterWithParameters(FilterComparisonType.Equal, "Status",
				Cr8Vantage.StatusInProgress, Cr8VantageApprovalStarter.CaseStatusAwaitingApproval,
				Cr8VantageApprovalStarter.CaseStatusRework));
			Entity openCase = caseEsq.GetEntityCollection(uc).FirstOrDefault();
			if (openCase != null) {
				output["OpenCaseId"] = openCase.PrimaryColumnValue.ToString("D");
				output["OpenCaseNumber"] = openCase.GetTypedColumnValue<string>("Number");
			}
			return output;
		}

		private static string PrimaryAddress(UserConnection uc, Guid contactId) {
			var esq = new EntitySchemaQuery(uc.EntitySchemaManager, "ContactAddress");
			esq.AddColumn("Address");
			EntitySchemaQueryColumn cityColumn = esq.AddColumn("City.Name");
			esq.Filters.Add(esq.CreateFilterWithParameters(FilterComparisonType.Equal, "Contact", contactId));
			esq.Filters.Add(esq.CreateFilterWithParameters(FilterComparisonType.Equal, "Primary", true));
			Entity row = esq.GetEntityCollection(uc).FirstOrDefault();
			if (row == null) {
				return string.Empty;
			}
			string address = row.GetTypedColumnValue<string>("Address");
			string city = row.GetTypedColumnValue<string>(cityColumn.Name);
			return string.IsNullOrWhiteSpace(city) ? address : address + ", " + city;
		}
	}

	// ---- T2 vantage_find_technician_slot -------------------------------------------------------

	public class Cr8FindTechnicianSlotAction : Cr8VantageActionBase
	{
		public Cr8FindTechnicianSlotAction() {
			Parameters = new List<SourceCodeActionParameter> {
				Cr8Vantage.In("JobName", "Technician qualification, e.g. Field Technician - Air Conditioning.", true),
				Cr8Vantage.In("Priority", "High | Medium | Low", true),
				Cr8Vantage.In("PreferredDate", "Optional preferred date yyyy-MM-dd"),
				Cr8Vantage.In("PreferredStartTime", "Optional preferred start time HH:mm, e.g. 16:00"),
				Cr8Vantage.Out("Status", "ok | none | error"),
				Cr8Vantage.Out("Reason", "Why none or error (e.g. unknown_job)"),
				Cr8Vantage.Out("TechnicianContactId", "Offered technician contact id"),
				Cr8Vantage.Out("TechnicianName", "Offered technician name"),
				Cr8Vantage.Out("WindowStart", "Arrival window start, yyyy-MM-ddTHH:mm:ss Central"),
				Cr8Vantage.Out("WindowEnd", "Arrival window end, yyyy-MM-ddTHH:mm:ss Central"),
				Cr8Vantage.Out("SpokenWindow", "Window as spoken words, e.g. today, 2 to 4 PM"),
				Cr8Vantage.Out("DisplayWindow", "Window for email and notes"),
				Cr8Vantage.Out("WithinTarget", "Meets the response target for the priority", DataValueType.BooleanDataValueTypeUId),
				Cr8Vantage.Out("PreferenceMatched", "yes | no | none")
			}.AsReadOnly();
		}

		public override LocalizableString GetCaption() {
			return new LocalizableString("Vantage: find technician slot");
		}

		public override LocalizableString GetDescription() {
			return new LocalizableString("Return the earliest free two-hour arrival window with a technician qualified for a Job, optionally at or after a preferred time. Read-only. Do not use to book.");
		}

		protected override Dictionary<string, object> Run(ActionExecutionOptions options) {
			UserConnection uc = Connection;
			DateTime? preferredDate = null;
			DateTime date;
			if (DateTime.TryParseExact(Cr8Vantage.Get(options, "PreferredDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture,
					DateTimeStyles.None, out date)) {
				preferredDate = date;
			}
			string preferredStart = Cr8Vantage.Get(options, "PreferredStartTime");
			Cr8SlotOffer offer = new Cr8TechnicianAvailabilityService().FindEarliestSlot(uc,
				Cr8Vantage.Get(options, "JobName"), Cr8Vantage.Get(options, "Priority"), preferredDate,
				preferredStart.Length == 0 ? null : preferredStart);
			return new Dictionary<string, object> {
				["Status"] = offer.Status,
				["Reason"] = offer.Reason ?? string.Empty,
				["TechnicianContactId"] = offer.TechnicianContactId == Guid.Empty ? string.Empty : offer.TechnicianContactId.ToString("D"),
				["TechnicianName"] = offer.TechnicianName ?? string.Empty,
				["WindowStart"] = Cr8Vantage.Format(offer.WindowStart),
				["WindowEnd"] = Cr8Vantage.Format(offer.WindowEnd),
				["SpokenWindow"] = offer.SpokenWindow ?? string.Empty,
				["DisplayWindow"] = offer.DisplayWindow ?? string.Empty,
				["WithinTarget"] = offer.WithinTarget,
				["PreferenceMatched"] = offer.PreferenceMatched ?? "none"
			};
		}
	}

	// ---- T3 vantage_book_visit -----------------------------------------------------------------

	public class Cr8BookVisitAction : Cr8VantageActionBase
	{
		public Cr8BookVisitAction() {
			Parameters = new List<SourceCodeActionParameter> {
				Cr8Vantage.In("ContactId", "Customer contact id from vantage_get_customer_context", true),
				Cr8Vantage.In("ConfItemId", "Equipment id from vantage_get_customer_context"),
				Cr8Vantage.In("TechnicianContactId", "Technician from vantage_find_technician_slot", true),
				Cr8Vantage.In("WindowStart", "WindowStart exactly as returned by vantage_find_technician_slot", true),
				Cr8Vantage.In("WindowEnd", "WindowEnd exactly as returned by vantage_find_technician_slot", true),
				Cr8Vantage.In("DisplayWindow", "DisplayWindow as returned by vantage_find_technician_slot", true),
				Cr8Vantage.In("Priority", "High | Medium | Low", true),
				Cr8Vantage.In("FeePosition", "Waived | Applies | May apply", true),
				Cr8Vantage.In("Subject", "Short fault summary, e.g. AC not cooling", true),
				Cr8Vantage.In("Symptoms", "Priority reason and the customer's own words", true),
				Cr8Vantage.In("ServiceAddress", "Confirmed service address: street, city", true),
				Cr8Vantage.Out("Booked", "True when the visit is booked (or was already booked)", DataValueType.BooleanDataValueTypeUId),
				Cr8Vantage.Out("Reason", "ok | duplicate | window_taken | error: <detail>"),
				Cr8Vantage.Out("CaseId", "Case id"),
				Cr8Vantage.Out("CaseNumber", "Case number to quote exactly"),
				Cr8Vantage.Out("VisitActivityId", "Visit activity id")
			}.AsReadOnly();
		}

		public override LocalizableString GetCaption() {
			return new LocalizableString("Vantage: book visit");
		}

		public override LocalizableString GetDescription() {
			return new LocalizableString("Book an accepted visit in one call: creates the case and the visit, assigns the technician and sets In progress, then returns the case number. Call only after the customer accepts the offered window.");
		}

		protected override void FillError(Dictionary<string, object> output, string message) {
			output["Booked"] = false;
			output["Reason"] = "error: " + message;
		}

		protected override Dictionary<string, object> Run(ActionExecutionOptions options) {
			UserConnection uc = Connection;
			var output = new Dictionary<string, object> { ["Booked"] = false, ["Reason"] = "ok" };
			Guid contactId = Cr8Vantage.GetGuid(options, "ContactId");
			Guid confItemId = Cr8Vantage.GetGuid(options, "ConfItemId");
			Guid technicianId = Cr8Vantage.GetGuid(options, "TechnicianContactId");
			DateTime? start = Cr8Vantage.GetDateTime(options, "WindowStart");
			DateTime? end = Cr8Vantage.GetDateTime(options, "WindowEnd");
			string priority = Cr8Vantage.Get(options, "Priority");
			Guid casePriorityId = Cr8Vantage.FindIdByName(uc, "CasePriority", priority);
			Guid activityPriorityId = Cr8Vantage.FindIdByName(uc, "ActivityPriority", priority);
			Guid feePositionId = Cr8Vantage.FindIdByName(uc, "Cr8FeePosition", Cr8Vantage.Get(options, "FeePosition"));
			Guid originId = SysSettings.GetValue(uc, "Cr8VantageCaseOrigin", Guid.Empty);
			if (contactId == Guid.Empty || technicianId == Guid.Empty || !start.HasValue || !end.HasValue
					|| casePriorityId == Guid.Empty || activityPriorityId == Guid.Empty || feePositionId == Guid.Empty
					|| originId == Guid.Empty) {
				output["Reason"] = "error: missing or unrecognised input (contact, technician, window, priority, fee position or origin setting)";
				return output;
			}

			// Idempotency: the same customer and equipment booked in the last 15 minutes.
			var dupEsq = new EntitySchemaQuery(uc.EntitySchemaManager, "Case");
			dupEsq.PrimaryQueryColumn.IsAlwaysSelect = true;
			dupEsq.AddColumn("Number");
			dupEsq.Filters.Add(dupEsq.CreateFilterWithParameters(FilterComparisonType.Equal, "Contact", contactId));
			if (confItemId != Guid.Empty) {
				dupEsq.Filters.Add(dupEsq.CreateFilterWithParameters(FilterComparisonType.Equal, "ConfItem", confItemId));
			}
			dupEsq.Filters.Add(dupEsq.CreateFilterWithParameters(FilterComparisonType.Equal, "Origin", originId));
			dupEsq.Filters.Add(dupEsq.CreateFilterWithParameters(FilterComparisonType.Equal, "Status", Cr8Vantage.StatusInProgress));
			dupEsq.Filters.Add(dupEsq.CreateFilterWithParameters(FilterComparisonType.GreaterOrEqual, "CreatedOn",
				Cr8VantageClock.ToRecord(uc, Cr8VantageClock.Now(uc).AddMinutes(-15))));
			Entity duplicate = dupEsq.GetEntityCollection(uc).FirstOrDefault();
			if (duplicate != null) {
				output["Booked"] = true;
				output["Reason"] = "duplicate";
				output["CaseId"] = duplicate.PrimaryColumnValue.ToString("D");
				output["CaseNumber"] = duplicate.GetTypedColumnValue<string>("Number");
				return output;
			}

			if (!new Cr8TechnicianAvailabilityService().IsWindowFree(uc, technicianId, start.Value, end.Value)) {
				output["Reason"] = "window_taken";
				return output;
			}

			Guid caseId = Guid.Empty;
			Guid visitId = Guid.Empty;
			try {
				Entity caseEntity = Cr8Vantage.NewEntity(uc, "Case");
				caseId = Guid.NewGuid();
				caseEntity.SetColumnValue("Id", caseId);
				caseEntity.SetColumnValue("Subject", Cr8Vantage.Get(options, "Subject"));
				caseEntity.SetColumnValue("Symptoms", Cr8Vantage.Get(options, "Symptoms"));
				caseEntity.SetColumnValue("ContactId", contactId);
				if (confItemId != Guid.Empty) {
					caseEntity.SetColumnValue("ConfItemId", confItemId);
				}
				caseEntity.SetColumnValue("PriorityId", casePriorityId);
				caseEntity.SetColumnValue("OriginId", originId);
				caseEntity.SetColumnValue("CategoryId", Cr8Vantage.CategoryServiceRequest);
				caseEntity.SetColumnValue("Cr8FeePositionId", feePositionId);
				caseEntity.SetColumnValue("Cr8ServiceAddress", Cr8Vantage.Get(options, "ServiceAddress"));
				caseEntity.SetColumnValue("StatusId", Cr8Vantage.StatusNew);
				Cr8Vantage.ApplySla(uc, caseEntity);
				caseEntity.Save(false);

				string givenName = ReadGivenName(uc, contactId);
				Entity visit = Cr8Vantage.NewEntity(uc, "Activity");
				visitId = Guid.NewGuid();
				visit.SetColumnValue("Id", visitId);
				visit.SetColumnValue("Title", Cr8Vantage.VisitTitlePrefix + " — " + givenName + " — " + Cr8Vantage.Get(options, "Subject"));
				visit.SetColumnValue("StartDate", Cr8VantageClock.ToRecord(uc, start.Value));
				visit.SetColumnValue("DueDate", Cr8VantageClock.ToRecord(uc, end.Value));
				visit.SetColumnValue("OwnerId", technicianId);
				visit.SetColumnValue("CaseId", caseId);
				visit.SetColumnValue("ContactId", contactId);
				visit.SetColumnValue("Notes", "Arrival window: " + Cr8Vantage.Get(options, "DisplayWindow"));
				visit.SetColumnValue("TypeId", Cr8Vantage.ActivityTypeTask);
				visit.SetColumnValue("ActivityCategoryId", Cr8VantageSettings.VisitCategoryId(uc));
				visit.SetColumnValue("StatusId", Cr8Vantage.ActivityStatusNotStarted);
				visit.SetColumnValue("PriorityId", activityPriorityId);
				visit.SetColumnValue("ShowInScheduler", true);
				visit.Save(false);

				// Owner and In progress LAST: the guard (R2) requires the visit to exist first.
				Entity update = uc.EntitySchemaManager.GetInstanceByName("Case").CreateEntity(uc);
				update.FetchFromDB(caseId);
				update.SetColumnValue("OwnerId", technicianId);
				update.SetColumnValue("StatusId", Cr8Vantage.StatusInProgress);
				update.Save(false);

				output["Booked"] = true;
				output["CaseId"] = caseId.ToString("D");
				output["CaseNumber"] = update.GetTypedColumnValue<string>("Number");
				output["VisitActivityId"] = visitId.ToString("D");
				return output;
			} catch (Exception ex) {
				Cr8Vantage.TryDelete(uc, "Activity", visitId);
				Cr8Vantage.TryDelete(uc, "Case", caseId);
				output["Booked"] = false;
				output["Reason"] = "error: " + ex.Message;
				return output;
			}
		}

		private static string ReadGivenName(UserConnection uc, Guid contactId) {
			Entity contact = uc.EntitySchemaManager.GetInstanceByName("Contact").CreateEntity(uc);
			return contact.FetchFromDB(contactId) ? contact.GetTypedColumnValue<string>("GivenName") : string.Empty;
		}
	}

	// ---- T4 vantage_request_coordinator --------------------------------------------------------

	public class Cr8RequestCoordinatorAction : Cr8VantageActionBase
	{
		public Cr8RequestCoordinatorAction() {
			Parameters = new List<SourceCodeActionParameter> {
				Cr8Vantage.In("Reason", "UnknownCaller | NoQualifiedTechnician | BookingFailed", true),
				Cr8Vantage.In("CallerName", "Caller name as given"),
				Cr8Vantage.In("CallerPhone", "Caller phone as given"),
				Cr8Vantage.In("ContactId", "Contact id if the caller was found"),
				Cr8Vantage.In("Priority", "High | Medium | Low", true),
				Cr8Vantage.In("Subject", "Short fault summary", true),
				Cr8Vantage.In("Symptoms", "Priority reason and the customer's words"),
				Cr8Vantage.In("ServiceAddress", "Service address if known"),
				Cr8Vantage.Out("Logged", "True when the request and coordinator task were created", DataValueType.BooleanDataValueTypeUId),
				Cr8Vantage.Out("CaseId", "Case id"),
				Cr8Vantage.Out("CaseNumber", "Case number to quote"),
				Cr8Vantage.Out("CoordinatorTaskId", "Coordinator task id"),
				Cr8Vantage.Out("Reason", "ok | error: <detail>")
			}.AsReadOnly();
		}

		public override LocalizableString GetCaption() {
			return new LocalizableString("Vantage: request coordinator");
		}

		public override LocalizableString GetDescription() {
			return new LocalizableString("Log an unassigned service request and a coordinator task when the caller is unknown, no qualified technician is free, or booking failed.");
		}

		protected override void FillError(Dictionary<string, object> output, string message) {
			output["Logged"] = false;
			output["Reason"] = "error: " + message;
		}

		protected override Dictionary<string, object> Run(ActionExecutionOptions options) {
			UserConnection uc = Connection;
			string priority = Cr8Vantage.Get(options, "Priority");
			Guid casePriorityId = Cr8Vantage.FindIdByName(uc, "CasePriority", priority);
			Guid activityPriorityId = Cr8Vantage.FindIdByName(uc, "ActivityPriority", priority);
			Guid coordinatorId = SysSettings.GetValue(uc, "Cr8VantageCoordinator", Guid.Empty);
			Guid originId = SysSettings.GetValue(uc, "Cr8VantageCaseOrigin", Guid.Empty);
			Guid contactId = Cr8Vantage.GetGuid(options, "ContactId");
			string reason = Cr8Vantage.Get(options, "Reason");
			string subject = Cr8Vantage.Get(options, "Subject");

			Entity caseEntity = Cr8Vantage.NewEntity(uc, "Case");
			Guid caseId = Guid.NewGuid();
			caseEntity.SetColumnValue("Id", caseId);
			caseEntity.SetColumnValue("Subject", subject);
			caseEntity.SetColumnValue("Symptoms", "Caller: " + Cr8Vantage.Get(options, "CallerName") + ", "
				+ Cr8Vantage.Get(options, "CallerPhone") + ". Reason: " + reason + ". " + Cr8Vantage.Get(options, "Symptoms"));
			if (contactId != Guid.Empty) {
				caseEntity.SetColumnValue("ContactId", contactId);
			}
			if (casePriorityId != Guid.Empty) {
				caseEntity.SetColumnValue("PriorityId", casePriorityId);
			}
			caseEntity.SetColumnValue("OriginId", originId);
			caseEntity.SetColumnValue("CategoryId", Cr8Vantage.CategoryServiceRequest);
			caseEntity.SetColumnValue("Cr8FeePositionId", Cr8Vantage.FindIdByName(uc, "Cr8FeePosition", "May apply"));
			caseEntity.SetColumnValue("Cr8ServiceAddress", Cr8Vantage.Get(options, "ServiceAddress"));
			caseEntity.SetColumnValue("StatusId", Cr8Vantage.StatusNew);
			Cr8Vantage.ApplySla(uc, caseEntity);
			caseEntity.Save(false);

			Entity task = Cr8Vantage.NewEntity(uc, "Activity");
			Guid taskId = Guid.NewGuid();
			DateTime now = Cr8VantageClock.Now(uc);
			task.SetColumnValue("Id", taskId);
			task.SetColumnValue("Title", "Coordinator: " + reason + " — " + subject);
			task.SetColumnValue("StartDate", Cr8VantageClock.ToRecord(uc, now));
			task.SetColumnValue("DueDate", Cr8VantageClock.ToRecord(uc, now.AddHours(1)));
			task.SetColumnValue("OwnerId", coordinatorId);
			task.SetColumnValue("CaseId", caseId);
			if (contactId != Guid.Empty) {
				task.SetColumnValue("ContactId", contactId);
			}
			task.SetColumnValue("TypeId", Cr8Vantage.ActivityTypeTask);
			task.SetColumnValue("ActivityCategoryId", Cr8Vantage.ActivityCategoryToDo);
			task.SetColumnValue("StatusId", Cr8Vantage.ActivityStatusNotStarted);
			task.SetColumnValue("PriorityId", activityPriorityId != Guid.Empty ? activityPriorityId
				: Cr8Vantage.FindIdByName(uc, "ActivityPriority", "High"));
			task.Save(false);

			Entity saved = uc.EntitySchemaManager.GetInstanceByName("Case").CreateEntity(uc);
			saved.FetchFromDB(caseId);
			return new Dictionary<string, object> {
				["Logged"] = true,
				["CaseId"] = caseId.ToString("D"),
				["CaseNumber"] = saved.GetTypedColumnValue<string>("Number"),
				["CoordinatorTaskId"] = taskId.ToString("D"),
				["Reason"] = "ok"
			};
		}
	}

	// ---- T5 vantage_get_technician_current_job -------------------------------------------------

	public class Cr8GetCurrentJobAction : Cr8VantageActionBase
	{
		public Cr8GetCurrentJobAction() {
			Parameters = new List<SourceCodeActionParameter> {
				Cr8Vantage.In("TechnicianContactId", "Contact id of the signed-in technician (from creatio_get_current_user)", true),
				Cr8Vantage.Out("Basis", "in_progress | most_recent | next_upcoming | none | not_a_technician | error"),
				Cr8Vantage.Out("CaseId", "Case id"),
				Cr8Vantage.Out("CaseNumber", "Case number"),
				Cr8Vantage.Out("Street", "Street of the service address"),
				Cr8Vantage.Out("EquipmentName", "Equipment name"),
				Cr8Vantage.Out("CustomerGivenName", "Customer first name"),
				Cr8Vantage.Out("SpokenWindow", "Visit window in words"),
				Cr8Vantage.Out("FeePosition", "Waived | Applies | May apply"),
				Cr8Vantage.Out("Subject", "Case subject"),
				Cr8Vantage.Out("ExistingSolution", "Solution already recorded, if any"),
				Cr8Vantage.Out("OtherCandidates", "Other jobs in the same group", DataValueType.IntegerDataValueTypeUId),
				Cr8Vantage.Out("Reason", "Error detail")
			}.AsReadOnly();
		}

		public override LocalizableString GetCaption() {
			return new LocalizableString("Vantage: get technician current job");
		}

		public override LocalizableString GetDescription() {
			return new LocalizableString("Return the job a Vantage technician is on now, just finished or has next, from their calendar and the clock. Read-only.");
		}

		protected override void FillError(Dictionary<string, object> output, string message) {
			output["Basis"] = "error";
			output["Reason"] = "error: " + message;
		}

		protected override Dictionary<string, object> Run(ActionExecutionOptions options) {
			Cr8CurrentJob job = new Cr8TechnicianJobService().GetCurrentJob(Connection,
				Cr8Vantage.GetGuid(options, "TechnicianContactId"));
			return new Dictionary<string, object> {
				["Basis"] = job.Basis,
				["CaseId"] = job.CaseId == Guid.Empty ? string.Empty : job.CaseId.ToString("D"),
				["CaseNumber"] = job.CaseNumber ?? string.Empty,
				["Street"] = job.Street ?? string.Empty,
				["EquipmentName"] = job.EquipmentName ?? string.Empty,
				["CustomerGivenName"] = job.CustomerGivenName ?? string.Empty,
				["SpokenWindow"] = job.SpokenWindow ?? string.Empty,
				["FeePosition"] = job.FeePosition ?? string.Empty,
				["Subject"] = job.Subject ?? string.Empty,
				["ExistingSolution"] = job.ExistingSolution ?? string.Empty,
				["OtherCandidates"] = job.OtherCandidates,
				["Reason"] = string.Empty
			};
		}
	}

	// ---- T6 vantage_create_kb_draft (native Draft article) -------------------------------------

	public class Cr8CreateKbDraftAction : Cr8VantageActionBase
	{
		public Cr8CreateKbDraftAction() {
			Parameters = new List<SourceCodeActionParameter> {
				Cr8Vantage.In("Topic", "Short topic of the policy gap, e.g. Follow-up visit fee", true),
				Cr8Vantage.In("ArticleBody", "Full draft text in the house format, with Decision needed: lines", true),
				Cr8Vantage.In("Keywords", "Optional search keywords"),
				Cr8Vantage.In("AffectedArticleId", "Id of the published article to revise; empty for a new article"),
				Cr8Vantage.In("SourceCaseId", "The low-rated case", true),
				Cr8Vantage.In("CustomerComment", "The customer's survey comment"),
				Cr8Vantage.Out("Created", "True when a new draft was created", DataValueType.BooleanDataValueTypeUId),
				Cr8Vantage.Out("DuplicateFound", "True when a draft with this title already exists", DataValueType.BooleanDataValueTypeUId),
				Cr8Vantage.Out("ArticleId", "Draft article id"),
				Cr8Vantage.Out("ReviewTaskId", "Review task id"),
				Cr8Vantage.Out("ReviewDueDate", "Review due date yyyy-MM-dd"),
				Cr8Vantage.Out("Reason", "ok | error: <detail>")
			}.AsReadOnly();
		}

		public override LocalizableString GetCaption() {
			return new LocalizableString("Vantage: create knowledge draft");
		}

		public override LocalizableString GetDescription() {
			return new LocalizableString("Create one unpublished Draft knowledge article (a revision of the affected article, or a new one) and a review task for the knowledge owner. Never publishes.");
		}

		protected override void FillError(Dictionary<string, object> output, string message) {
			output["Created"] = false;
			output["Reason"] = "error: " + message;
		}

		protected override Dictionary<string, object> Run(ActionExecutionOptions options) {
			UserConnection uc = Connection;
			string topic = Cr8Vantage.Get(options, "Topic");
			Guid affectedId = Cr8Vantage.GetGuid(options, "AffectedArticleId");
			Guid sourceCaseId = Cr8Vantage.GetGuid(options, "SourceCaseId");
			string affectedTitle = string.Empty;
			if (affectedId != Guid.Empty) {
				Entity affected = uc.EntitySchemaManager.GetInstanceByName("KnowledgeBase").CreateEntity(uc);
				if (affected.FetchFromDB(affectedId)) {
					affectedTitle = (affected.GetTypedColumnValue<string>("Name") ?? string.Empty).Trim();
				} else {
					affectedId = Guid.Empty;
				}
			}
			string name = affectedTitle.Length > 0 ? "DRAFT — " + affectedTitle + " (revision)" : "DRAFT — " + topic;
			var output = new Dictionary<string, object> { ["Created"] = false, ["DuplicateFound"] = false, ["Reason"] = "ok" };

			var dupEsq = new EntitySchemaQuery(uc.EntitySchemaManager, "KnowledgeBase");
			dupEsq.PrimaryQueryColumn.IsAlwaysSelect = true;
			dupEsq.Filters.Add(dupEsq.CreateFilterWithParameters(FilterComparisonType.Equal, "Name", name));
			dupEsq.Filters.Add(dupEsq.CreateFilterWithParameters(FilterComparisonType.Equal, "Status", Cr8Vantage.KbStatusDraft));
			Entity existing = dupEsq.GetEntityCollection(uc).FirstOrDefault();
			Guid articleId;
			if (existing != null) {
				articleId = existing.PrimaryColumnValue;
				output["DuplicateFound"] = true;
			} else {
				Entity article = Cr8Vantage.NewEntity(uc, "KnowledgeBase");
				articleId = Guid.NewGuid();
				article.SetColumnValue("Id", articleId);
				article.SetColumnValue("Name", name);
				article.SetColumnValue("Notes", Cr8Vantage.Get(options, "ArticleBody"));
				article.SetColumnValue("TypeId", Cr8Vantage.KbTypeRulesAndRegulations);
				article.SetColumnValue("StatusId", Cr8Vantage.KbStatusDraft);
				string keywords = Cr8Vantage.Get(options, "Keywords");
				if (keywords.Length > 0) {
					article.SetColumnValue("Keywords", keywords);
				}
				article.Save(false);
				output["Created"] = true;
			}
			output["ArticleId"] = articleId.ToString("D");

			string caseNumber = string.Empty;
			if (sourceCaseId != Guid.Empty) {
				Entity sourceCase = uc.EntitySchemaManager.GetInstanceByName("Case").CreateEntity(uc);
				if (sourceCase.FetchFromDB(sourceCaseId)) {
					caseNumber = sourceCase.GetTypedColumnValue<string>("Number");
				} else {
					sourceCaseId = Guid.Empty;
				}
			}
			DateTime now = Cr8VantageClock.Now(uc);
			DateTime due = new Cr8VantageBusinessCalendar().AddBusinessDays(uc, now, 2).Date.AddHours(17);
			Entity task = Cr8Vantage.NewEntity(uc, "Activity");
			Guid taskId = Guid.NewGuid();
			task.SetColumnValue("Id", taskId);
			task.SetColumnValue("Title", "Review draft article: " + topic + (caseNumber.Length > 0 ? " (from " + caseNumber + ")" : string.Empty));
			task.SetColumnValue("StartDate", Cr8VantageClock.ToRecord(uc, now));
			task.SetColumnValue("DueDate", Cr8VantageClock.ToRecord(uc, due));
			task.SetColumnValue("OwnerId", SysSettings.GetValue(uc, "Cr8VantageKnowledgeOwner", Guid.Empty));
			task.SetColumnValue("KnowledgeBaseId", articleId);
			if (sourceCaseId != Guid.Empty) {
				task.SetColumnValue("CaseId", sourceCaseId);
			}
			task.SetColumnValue("Notes", "Draft article " + articleId.ToString("D")
				+ (affectedId != Guid.Empty ? " revises article " + affectedId.ToString("D") : " (new article)")
				+ ". Source case " + caseNumber + ". Customer comment: \"" + Cr8Vantage.Get(options, "CustomerComment") + "\"");
			task.SetColumnValue("TypeId", Cr8Vantage.ActivityTypeTask);
			task.SetColumnValue("ActivityCategoryId", Cr8Vantage.ActivityCategoryToDo);
			task.SetColumnValue("StatusId", Cr8Vantage.ActivityStatusNotStarted);
			task.SetColumnValue("PriorityId", Cr8Vantage.FindIdByName(uc, "ActivityPriority", "Medium"));
			task.Save(false);
			output["ReviewTaskId"] = taskId.ToString("D");
			output["ReviewDueDate"] = due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
			return output;
		}
	}

	// ---- T9 vantage_send_booking_confirmation --------------------------------------------------

	public class Cr8SendBookingConfirmationAction : Cr8VantageActionBase
	{
		public Cr8SendBookingConfirmationAction() {
			Parameters = new List<SourceCodeActionParameter> {
				Cr8Vantage.In("CaseId", "Case id returned by vantage_book_visit", true),
				Cr8Vantage.In("Subject", "Email subject, starting with: Your Vantage visit is booked", true),
				Cr8Vantage.In("Body", "Filled confirmation email body (plain text)", true),
				Cr8Vantage.Out("Sent", "True when the email was sent now", DataValueType.BooleanDataValueTypeUId),
				Cr8Vantage.Out("AlreadySent", "True when a confirmation was sent before for this case", DataValueType.BooleanDataValueTypeUId),
				Cr8Vantage.Out("EmailActivityId", "Email activity id (proof of send)"),
				Cr8Vantage.Out("Reason", "ok | no_email | not_found | error: <detail>")
			}.AsReadOnly();
		}

		public override LocalizableString GetCaption() {
			return new LocalizableString("Vantage: send booking confirmation");
		}

		public override LocalizableString GetDescription() {
			return new LocalizableString("Send the single booking confirmation email for a case that vantage_book_visit has just booked. The recipient is the case contact. Returns AlreadySent if the confirmation was sent before. Call once per booking.");
		}

		protected override void FillError(Dictionary<string, object> output, string message) {
			output["Sent"] = false;
			output["AlreadySent"] = false;
			output["Reason"] = "error: " + message;
		}

		protected override Dictionary<string, object> Run(ActionExecutionOptions options) {
			UserConnection uc = Connection;
			var output = new Dictionary<string, object> { ["Sent"] = false, ["AlreadySent"] = false, ["Reason"] = "ok" };
			Guid caseId = Cr8Vantage.GetGuid(options, "CaseId");
			Entity caseEntity = uc.EntitySchemaManager.GetInstanceByName("Case").CreateEntity(uc);
			if (caseId == Guid.Empty || !caseEntity.FetchFromDB(caseId)) {
				output["Reason"] = "not_found";
				return output;
			}

			var sentEsq = new EntitySchemaQuery(uc.EntitySchemaManager, "Activity");
			sentEsq.PrimaryQueryColumn.IsAlwaysSelect = true;
			sentEsq.Filters.Add(sentEsq.CreateFilterWithParameters(FilterComparisonType.Equal, "Case", caseId));
			sentEsq.Filters.Add(sentEsq.CreateFilterWithParameters(FilterComparisonType.Equal, "Type", Cr8Vantage.ActivityTypeEmail));
			sentEsq.Filters.Add(sentEsq.CreateFilterWithParameters(FilterComparisonType.StartWith, "Title", Cr8Vantage.ConfirmationTitlePrefix));
			Entity previous = sentEsq.GetEntityCollection(uc).FirstOrDefault();
			if (previous != null) {
				output["AlreadySent"] = true;
				output["EmailActivityId"] = previous.PrimaryColumnValue.ToString("D");
				return output;
			}

			Guid contactId = caseEntity.GetTypedColumnValue<Guid>("ContactId");
			Entity contact = uc.EntitySchemaManager.GetInstanceByName("Contact").CreateEntity(uc);
			string email = contactId != Guid.Empty && contact.FetchFromDB(contactId)
				? (contact.GetTypedColumnValue<string>("Email") ?? string.Empty).Trim()
				: string.Empty;
			if (email.Length == 0) {
				output["Reason"] = "no_email";
				return output;
			}

			string subject = Cr8Vantage.Get(options, "Subject");
			if (!subject.StartsWith(Cr8Vantage.ConfirmationTitlePrefix, StringComparison.Ordinal)) {
				subject = Cr8Vantage.ConfirmationTitlePrefix + " — " + caseEntity.GetTypedColumnValue<string>("Number");
			}
			string body = Cr8Vantage.Get(options, "Body");
			DateTime now = Cr8VantageClock.Now(uc);
			Entity mail = Cr8Vantage.NewEntity(uc, "Activity");
			Guid mailId = Guid.NewGuid();
			mail.SetColumnValue("Id", mailId);
			mail.SetColumnValue("TypeId", Cr8Vantage.ActivityTypeEmail);
			mail.SetColumnValue("ActivityCategoryId", Cr8Vantage.ActivityCategoryEmail);
			mail.SetColumnValue("Title", subject);
			mail.SetColumnValue("Body", System.Net.WebUtility.HtmlEncode(body).Replace("\n", "<br/>"));
			mail.SetColumnValue("IsHtmlBody", true);
			mail.SetColumnValue("Sender", Cr8Vantage.SenderAddress);
			mail.SetColumnValue("Recepient", email + ";");
			mail.SetColumnValue("CaseId", caseId);
			mail.SetColumnValue("ContactId", contactId);
			mail.SetColumnValue("StartDate", Cr8VantageClock.ToRecord(uc, now));
			mail.SetColumnValue("DueDate", Cr8VantageClock.ToRecord(uc, now));
			mail.SetColumnValue("StatusId", Cr8Vantage.ActivityStatusNotStarted);
			mail.SetColumnValue("PriorityId", Cr8Vantage.FindIdByName(uc, "ActivityPriority", "Medium"));
			mail.Save(false);

			Cr8VantageMail.SendActivityEmail(uc, mailId);
			output["Sent"] = true;
			output["EmailActivityId"] = mailId.ToString("D");
			return output;
		}
	}
}
