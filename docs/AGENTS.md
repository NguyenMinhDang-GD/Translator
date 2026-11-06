# Project Workflow & Git Safety Rules

## Incremental Local Git Commits (Mandatory)
- **Commit After Passing Tests**: While implementing features, bug fixes, or refactors, break work into logical steps. Immediately after completing each step, verify that `dotnet build` and `dotnet test tests/Translator.Tests/Translator.Tests.csproj` pass with zero warnings/errors, then create a clean local Git commit (`git add` relevant tracked/source files and `git commit -m "<type>(<scope>): <summary>"`).
- **Respect `.gitignore` & Stage Intentionally**:
  - `tests/`, `docs/`, `publish/`, `.vscode/`, `.translator_cache/`, `.translator_backup/`, temporary dumps, and game binaries are intentionally excluded in `.gitignore`.
  - **Never** force-add (`git add -f`) ignored folders such as `tests/` or `docs/`. Always check `git status -s` before committing.
- **Running App File-Lock Safety (Windows)**:
  - If `Translator.Desktop.exe` is currently open on the user's machine, verify Desktop builds via an isolated output folder (e.g., `dotnet build src/Translator.Desktop/Translator.Desktop.csproj -o src/Translator.Desktop/obj/verify_build`) and check `Get-Process -Name "Translator.Desktop"` before republishing `publish\Translator.Desktop.exe`.
- **Local Commits Only — Never Push**: Do **NOT** run `git push` or push commits to GitHub / any remote repository unless the user explicitly asks to push in that turn.
