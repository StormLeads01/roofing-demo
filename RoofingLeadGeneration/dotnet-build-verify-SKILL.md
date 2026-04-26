---
name: dotnet-build-verify
description: >
  Use this skill whenever making any code changes to the RoofingLeadGeneration ASP.NET Core project
  (C#, Razor views, controllers, models, JavaScript, CSS). After every edit — no matter how small —
  this skill requires verifying the project compiles before reporting the task as complete.
  Triggers on: editing .cshtml, .cs, .js, .css files in the RoofingLeadGeneration project;
  fixing bugs; adding features; refactoring; any file modification in the project codebase.
  Never mark a coding task done without running a build verification first.
---

# .NET Build Verify Skill

Every code change in RoofingLeadGeneration must be followed by a successful build before you
report completion. A change that hasn't been compiled is a change that might be broken — and
the goal is to hand James working code, not working-looking code.

## Project details

- **Solution path (Windows):** `C:\Users\James\source\repos\roofing-demo\RoofingLeadGeneration`
- **Bash mount path:** `/sessions/sharp-busy-mendel/mnt/RoofingLeadGeneration/`
- **Framework:** ASP.NET Core 8 MVC
- **Key file types:** `.cs` (C#), `.cshtml` (Razor views), `.js`, `.css`

## Step 1 — Run `dotnet build`

After making all your edits, try to build using dotnet. The dotnet executable is a Windows binary
and may not be in the Linux sandbox PATH. Try it anyway:

```bash
cd /sessions/sharp-busy-mendel/mnt/RoofingLeadGeneration && dotnet build 2>&1 | tail -40
```

**If `dotnet` is found:** Read the output. Look for the summary line. If the build
reports `0 Error(s)`, you're done — report success. If there are errors, proceed to Step 3.

**If `dotnet: command not found`:** Fall back to Step 2 (static analysis).

## Step 2 — Static analysis fallback (when dotnet unavailable)

Since the Linux sandbox can't run Windows binaries, do a thorough manual check of every file
you touched. This is not a substitute for a real build — it's the best available approximation.

### For `.cshtml` Razor files — check these patterns:

**RZ1031 — C# in tag helper attribute declaration area**
Bad:  `<option value="x" @(condition ? "selected" : "")>`
Good: `<option value="x" selected="@(condition ? "selected" : null)">`

**Double-brace parse errors — inline Dictionary literals**
Bad:  `@("status-" + (new Dictionary<string,string>{{"a","b"}})[key])`
Good: Move the expression to the `@{ }` block at the top of the view, assign to a variable, use the variable inline.

**`@Html.AttributeEncode` — does not exist in ASP.NET Core**
Replace with just `@Model.Property` — Razor auto-encodes attribute values.

**Inline C# blocks in HTML attributes**
Razor expressions in attribute *values* must use `@(...)` form. Raw `@{ }` blocks cannot appear inside HTML tags.

**`@` in non-expression context**
Every `@` in a Razor file that isn't followed by `{`, `(`, a keyword, or a model accessor is a parse error.

### For `.cs` C# files — check these patterns:

- Missing `using` directives for types you added
- `async` methods must return `Task` or `Task<T>`, not `void` (unless event handlers)
- EF Core `Include()` calls need the right namespace (`Microsoft.EntityFrameworkCore`)
- Null reference: if a navigation property could be null, guard it before access
- Controller action return types must be `IActionResult` or `Task<IActionResult>`

### For `.js` files:
- Syntax errors (unclosed braces, missing semicolons after function expressions)
- References to DOM elements that may not exist (guard with `if (!el) return`)

## Step 3 — Fix → rebuild loop

When you find errors (either from `dotnet build` output or static analysis):

1. Read the full error carefully. Note the **file path**, **line number**, and **error code** (CS#### or RZ####).
2. Read that section of the file with the Read tool (offset to ±10 lines around the error).
3. Fix the root cause — don't just suppress the symptom.
4. After fixing, return to Step 1 and build again.
5. Repeat until clean.

Common error → fix mappings:

| Error | Likely cause | Fix |
|-------|-------------|-----|
| CS1061 | Method/property doesn't exist on type | Check ASP.NET Core API; `Html.AttributeEncode` → `@Model.Prop` |
| CS0103 | Undefined variable | Move computation to `@{ }` block or add missing `using` |
| CS0246 | Type not found | Add `@using` directive at top of .cshtml or `using` in .cs |
| RZ1031 | C# in tag helper attribute area | Move expression to attribute *value* using `attr="@(...)"` |
| RZ1006 | Unclosed `@{` block | Check for mismatched braces in Razor code blocks |
| CS8600/CS8602 | Nullable reference warning as error | Add null check or use `?.` operator |

## Step 4 — Report completion

Only after a clean build (or a clean static analysis pass when dotnet is unavailable), tell James:

> "✓ Build passed — [brief summary of what you changed]"

If you had to fix additional errors beyond the original task, mention them briefly:
> "Also fixed a CS1061 on Detail.cshtml line 252 (removed invalid Html.AttributeEncode call)."

Never say a task is done if you haven't verified it compiles. If you genuinely cannot verify
(e.g., the change is purely to a `.css` or static asset with no C# impact), state that explicitly.
