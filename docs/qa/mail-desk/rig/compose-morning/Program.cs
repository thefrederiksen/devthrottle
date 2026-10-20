using System.Globalization;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory.Email;
using CcDirector.Gateway.MailDesk;
using CcDirector.Gateway.Settings;

// The Mail Desk rig's 08:00 email, composed on demand: the Gateway's own fold (MailDeskEmailFold, wired exactly as
// GatewayHost wires it for the factory email sweep) and its own composer (FactoryEmailComposer), over the rig's own
// throwaway gateway.db, for the account's next 08:00 in the given time zone. It sends nothing and writes nothing of its
// own: it prints the composed email as JSON. The rig runs it with CC_DIRECTOR_ROOT set to a folder of its own, so its
// log stays in the rig.
//
//   dotnet ComposeMorning.dll <path to gateway.db> <time zone id>
if (args.Length != 2)
{
    Console.Error.WriteLine("usage: ComposeMorning <path to gateway.db> <time zone id>");
    return 2;
}

var dbPath = args[0];
if (!File.Exists(dbPath))
{
    Console.Error.WriteLine($"no database at {dbPath}");
    return 2;
}
var zone = TimeZoneInfo.FindSystemTimeZoneById(args[1]);
var tenant = TenantId.Local;
var db = new GatewayDatabase(new SingleTenantContext(), dbPath);

var mailDeskSwitch = new MailDeskSwitch(new TenantSettingsStore(db));
var channels = new MailDeskChannelStore(db);
var decisions = new MailDeskDecisionStore(db);
var rules = new MailDeskRulesStore(db);
var settings = new MailDeskSettingsStore(db);
var routed = new MailDeskRoutedStore(db);
var fold = new MailDeskEmailFold(mailDeskSwitch.IsOn, channels, decisions, rules,
    t => MailDeskSpend.Of(t, decisions, settings), routed);

// The account's next 08:00 in the zone: its email covers the 24 hours up to it, so everything the rig did so far is in it.
var nowUtc = DateTime.UtcNow;
var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
var eight = local.Date.AddHours(8);
if (eight <= local) eight = eight.AddDays(1);
var slot = FactoryEmailSlots.AtBoundary(TimeZoneInfo.ConvertTimeToUtc(eight, zone), zone);
var section = fold.Section(tenant, slot.DayStartUtc, slot.BoundaryUtc, zone);
var email = FactoryEmailComposer.Compose(new FactoryEmailInput(slot, zone, Array.Empty<CcDirector.Gateway.Contracts.CronRunProblemDto>(),
    Array.Empty<CcDirector.Gateway.Contracts.CronFactoryDayDto>(), new Dictionary<string, FactoryEmailFactory>(), null, section));

Console.WriteLine(JsonSerializer.Serialize(new
{
    slot = slot.Key,
    kind = slot.Kind,
    windowFromUtc = slot.DayStartUtc.ToString("o", CultureInfo.InvariantCulture),
    windowToUtc = slot.BoundaryUtc.ToString("o", CultureInfo.InvariantCulture),
    mailDeskSection = section is not null,
    subject = email.Subject,
    text = email.Text,
}));
return 0;
