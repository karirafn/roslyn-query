# roslyn-query

A global dotnet CLI tool for semantic C# codebase queries via the Roslyn API. Built as a companion tool for AI coding agents (Claude, etc.) that need to understand code structure beyond text search -- finding references, callers, implementations, inheritance chains, and unused symbols with full semantic accuracy.

## Installation

Requires .NET 10 SDK.

```bash
# Clone and install as a global tool
git clone https://github.com/karirafn/roslyn-query.git
cd roslyn-query
dotnet pack
dotnet tool install --global --add-source ./src/bin/Release roslyn-query
```

To update after pulling new changes:

```bash
dotnet pack
dotnet tool update --global --add-source ./src/bin/Release roslyn-query
```

## Usage

```text
roslyn-query <command> <symbol> [solution.sln|.slnx] [flags]
```

If the solution path is omitted, the tool walks up from the current directory to find a `.sln` or `.slnx` file.

### Symbol format

- Type: `OrderAggregate`
- Member: `OrderAggregate.PlaceOrder`
- Attribute: `Authorize` or `[Authorize]`
- Namespace prefix: `MyApp.Orders`

If a symbol name is ambiguous, qualify it as `TypeName.MemberName`. If multiple matches still exist, pass `--all` to get results for all of them.

## Commands

### find-refs

All references to a type, property, or method. Excludes the declaration site.

```bash
roslyn-query find-refs OrderAggregate
roslyn-query find-refs OrderAggregate.PlaceOrder
```

Output: `file:line` per reference.

```text
src/Orders/PlaceOrderHandler.cs:14
src/Orders/OrderController.cs:27
```

### find-callers

Invocation call sites only -- excludes typeof, nameof, method group references, and casts.

```bash
roslyn-query find-callers PlaceOrder
roslyn-query find-callers OrderAggregate.PlaceOrder
```

Output: `file:line\tcalling-symbol` per call site.

```text
src/Orders/PlaceOrderHandler.cs:14	MyApp.Orders.PlaceOrderHandler.Handle(PlaceOrderCommand)
```

### find-ctor

All `new T(...)` construction sites.

```bash
roslyn-query find-ctor OrderAggregate
```

Output: `file:line` per call site.

```text
src/Orders/PlaceOrderHandler.cs:22
```

### find-impl

All implementations of an interface or subclasses of a class.

```bash
roslyn-query find-impl IOrderRepository
roslyn-query find-impl OrderBase
```

Output: `file:line\tfully-qualified-type` per implementation.

```text
src/Infrastructure/SqlOrderRepository.cs:5	MyApp.Infrastructure.SqlOrderRepository
```

### find-overrides

All overrides of a virtual or abstract member.

```bash
roslyn-query find-overrides OrderAggregate.Validate
```

Output: `file:line\tContainingType.MemberName` per override.

```text
src/Orders/SpecialOrder.cs:18	MyApp.Orders.SpecialOrder.Validate
```

### find-attribute

All symbols decorated with an attribute.

```bash
roslyn-query find-attribute Authorize
roslyn-query find-attribute [HttpGet]
```

Output: `file:line\tfully-qualified-symbol` per match.

```text
src/Orders/OrderController.cs:12	MyApp.Orders.OrderController.GetOrders()
```

### find-base

Inheritance chain and all implemented interfaces of a type.

```bash
roslyn-query find-base OrderAggregate
```

Output: `base\ttype\tfile:line` for base classes, `interface\ttype\tfile:line` for interfaces. External types show `(external)` instead of a file location.

```text
base	MyApp.Domain.AggregateRoot	src/Domain/AggregateRoot.cs:3
interface	System.IDisposable	(external)
```

### find-unused

All symbols with zero source references outside their own declaration. Excludes compiler-generated members, interface implementations, entry points, and parameterized constructors. Output is advisory -- reflection-activated types may appear.

```bash
roslyn-query find-unused
roslyn-query find-unused MySolution.sln
```

Output: `file:line\tfully-qualified-symbol` per unused symbol.

```text
src/Orders/LegacyOrderService.cs:5	MyApp.Orders.LegacyOrderService
src/Orders/LegacyOrderService.cs:12	MyApp.Orders.LegacyOrderService.ProcessOrder(Guid)
```

### list-members

All members of a type: properties, methods, fields, events, and constructors. Works on source types and NuGet/external types.

```bash
roslyn-query list-members OrderAggregate
roslyn-query list-members DbContext --inherited
```

Output: `kind\tdisplay` per member.

```text
property	Guid Id
method	void PlaceOrder(Guid customerId)
constructor	OrderAggregate(Guid id, string name)
field	int MaxRetries
event	EventHandler OrderPlaced
```

With `--inherited`, a third column shows the declaring type:

```text
method	object.ToString()	System.Object
```

### list-types

All types in a namespace (prefix match).

```bash
roslyn-query list-types MyApp.Orders
```

Output: `kind\tfully-qualified-type\tfile:line` per type.

```text
class	MyApp.Orders.OrderAggregate	src/Orders/OrderAggregate.cs:5
interface	MyApp.Orders.IOrderRepository	src/Orders/IOrderRepository.cs:3
```

### list-projects

All projects in the solution with name and file path.

```bash
roslyn-query list-projects
roslyn-query list-projects --absolute
```

Output: `name\tpath` per project. Paths are relative to the solution directory by default; use `--absolute` for absolute paths.

```text
MyApp.Api	src/MyApp.Api/MyApp.Api.csproj
MyApp.Domain	src/MyApp.Domain/MyApp.Domain.csproj
MyApp.Tests	tests/MyApp.Tests/MyApp.Tests.csproj
```

### describe

Summary card for a type: kind, fully-qualified name, source location, base type, implemented interfaces, and member counts.

```bash
roslyn-query describe CommandDispatcher
```

Output:

```text
class RoslynQuery.CommandDispatcher  src/CommandDispatcher.cs:9
base:       SomeBase
interfaces: IFoo, IBar
members:    2 ctors, 5 props, 3 methods
```

The `base` line is omitted when the type has no base type (or only inherits from `System.Object`).
The `interfaces` line is omitted when the type implements no interfaces.
The `members` line is omitted when the type has no members.

Supports the `--absolute` flag to emit absolute file paths in the header line.

## Flags

| Flag | Description |
|---|---|
| `--quiet`, `-q` | Suppress workspace loading warnings |
| `--context` | Add trimmed source line as a tab-separated column on `file:line` results |
| `--all` | Return results for all matching symbols when the name is ambiguous (grouped by `# Symbol` headers) |
| `--inherited` | Include inherited members in `list-members` output (adds declaring type as third column) |
| `--absolute` | Emit absolute file paths (default: relative to solution directory) |
| `--limit N` | Cap output to N lines per query; prints `... (N more, omit --limit to see all)` to stderr when truncated |
| `--compact` | Emit short symbol names (`TypeName.MemberName`) instead of fully-qualified display strings — applies to `find-callers` and `find-overrides` |
| `--count` | Print only the integer result count to stdout; suppresses file:line output. Not supported on `find-base` or `list-members`. Mutually exclusive with `--limit` |
| `--in-project <name>` | Scope results to a single project (case-insensitive exact match on project name). Not supported on `find-base`, `list-members`, `describe`, or `list-projects`. Use `list-projects` to discover valid project names. |

## Batch queries

The `batch` command reads newline-delimited commands and runs each against the warm daemon, emitting results separated by `=== {command} ===` headers. This avoids multiple cold-start roundtrips when exploring a codebase.

Commands can be read from a file or from stdin:

```bash
# From a file
roslyn-query batch queries.txt

# From stdin
printf 'find-refs OrderAggregate\nfind-callers PlaceOrder\nlist-members IOrderRepository\n' \
  | roslyn-query batch
```

Output:

```text
=== find-refs OrderAggregate ===
src/Orders/PlaceOrderHandler.cs:14
src/Orders/OrderController.cs:27
=== find-callers PlaceOrder ===
src/Orders/PlaceOrderHandler.cs:14	MyApp.Orders.PlaceOrderHandler.Handle(PlaceOrderCommand)
=== list-members IOrderRepository ===
method	Task<OrderAggregate> GetByIdAsync(Guid id)
method	Task SaveAsync(OrderAggregate order)
```

Global flags passed to `batch` (e.g. `--limit`, `--compact`, `--absolute`) are forwarded to every sub-command.

## Daemon mode

The tool automatically starts a background daemon process on first use to keep the Roslyn workspace loaded in memory.
Subsequent queries complete in under 1 second, compared to 3-8 seconds for a cold start.

- The daemon exits automatically after 30 minutes of inactivity
- Each solution gets its own daemon process -- two different solutions run independent daemons
- If the solution file changes on disk, the daemon reloads the workspace automatically

To stop a daemon manually:

```bash
roslyn-query daemon stop
roslyn-query daemon stop MySolution.sln
```

If the solution path is omitted, the tool searches parent directories for a `.sln` or `.slnx` file, same as normal commands.

## Performance notes

- With daemon mode, only the first query pays the workspace loading cost (3-8 seconds depending on solution size). Subsequent queries complete in under 1 second.
- Use `--quiet` to suppress noisy MSBuild warnings when only the results matter.
- `find-unused` calls `FindReferencesAsync` per symbol -- slow on large solutions. Use on targeted namespaces when possible.
