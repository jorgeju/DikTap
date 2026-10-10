# Contributing to DikTap

Thanks for your interest! DikTap is a small project with a single maintainer ([@jorgeju](https://github.com/jorgeju)).
Everything that lands in `main` goes through his review and approval. You can write in English or Spanish.

## Project focus

DikTap aims to be **ultra-light and fast, with few options**: a resident process you barely notice. Every proposal is welcome,
even ones that would add weight or new layers, and all of them get considered. But not everything will be included, however well it
works, if it does not fit that philosophy (which may evolve in the future). For anything big, open an issue **before** writing code.
If an idea does not fit, you can keep it in your own fork: the license allows it.

## How to take part

- **Bugs and ideas:** open an [issue](https://github.com/jorgeju/DikTap/issues/new/choose) using the matching template.
- **Code:** fork the repository, create a branch and open a pull request against `main`.
- **Vulnerabilities:** do not use public issues; follow [SECURITY.md](SECURITY.md).

## Build and test

Requirements: .NET 10 SDK on Windows (Visual Studio is not needed).

```powershell
./scripts/Build.ps1                # restore + Release build + unit tests
./scripts/Publish.ps1 -Package     # self-contained EXE and ZIP in artifacts/
```

The tests in `tests/DikTap.Tests` never touch the desktop. `tests/DikTap.LiveCheck` (and `scripts/Test-Desktop.ps1`) do:
they generate synthetic keyboard and clipboard input, so do not run them while you are using the computer.

## What a good pull request looks like

- One thing per PR, with tests when there is new logic or a fixed bug. CI must pass.
- User-visible texts go in `src/DikTap/Localization/Str.cs` in **Spanish and English** at the same time.
- DikTap is a resident process: avoid new dependencies, idle timers and anything that increases idle memory, threads or CPU.
  No telemetry and no network calls other than the OpenAI API.
- Never log keys, audio or dictated text.

## License

By submitting a contribution you agree that it is published under the project's [GPL-3.0-only](LICENSE) license.

---

**Español:** las contribuciones son bienvenidas, en español o en inglés. DikTap busca ser ultraligero y rápido, con pocas opciones;
se valoran todas las ideas, pero no todas se incluirán si no encajan con ese enfoque. Para algo grande, abre antes un issue.
Los textos visibles van en español e inglés en `Str.cs`, y al contribuir aceptas que tu código se publique bajo GPL-3.0-only.
