using Docuconf;
using Shop;

// `dotnet Shop.dll docuconf export contract.cue` writes the contract and exits.
if (DocuconfExport.RunIfRequested(args)) return;

var builder = Host.CreateApplicationBuilder(args);
builder.AddDocuconf<ShopOptions>();   // binds ShopOptions and validates it, the role and otel@1's variables
using var host = builder.Build();

// The validated options, or every problem on stderr and exit status 1.
var shop = host.Services.LoadOrExit<ShopOptions>();

// docuconf declares and checks DOCUCONF_ROLE, so here it is web or worker.
var role = builder.Configuration[DocuconfContract.RoleVar]!;

// A real app would start its web server or worker loop here; the example says what it would run and exits.
Console.WriteLine(ShopApp.Describe(role, shop));
