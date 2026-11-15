# Office LTSC deployment backend (#112)

Use one OfficeDeploymentService(new WindowsOfficeDeployment()). Present LicenseNotice and obtain explicit installation/license acknowledgement before InstallAsync(true, progress, cancellationToken). CheckAsync is read-only. This backend does not change UI or register an AI tool.

The fixed recipe is ProPlus2024Volume, x64, pt-BR, PerpetualVL2024. Display None and accepted EULA remove the Office wizard after confirmation. No keys, activation commands, removals, forced application closure or reboot are accepted. Existing Office/365/Visio/Project registrations and running Office apps/installers conservatively block deployment; inventory failures also block. Preflight repeats before authorization.

Nexo stays asInvoker. It downloads pinned official Microsoft ODT release 16.0.20430.20146, limits each HTTPS redirect to download.microsoft.com, caps download at 30 MB and checks Microsoft Authenticode. The source is opened against writes while signature and SHA-256 are checked. Only protected System-directory Windows PowerShell is elevated, with fixed inline bootstrap and pinned hash in its command line. No executable, script or XML from user temp is directly executed elevated.

After UAC, the helper generates its own random staging under Program Files and creates it atomically with protected ACL granting only Administrators and SYSTEM full control. It rejects preexisting paths, reparse points and unexpected owners/ACLs. It copies ODT into protected staging and requires the pre-UAC hash plus valid Microsoft Authenticode before extraction. It checks extracted setup signature, writes the constant recipe itself and waits for configure. Module discovery is restricted to protected PowerShell-home modules. Replacing temp files, renaming parents or killing Nexo during UAC cannot substitute elevated code/XML: changed source fails protected-copy hash/signature checks.

Helper staging is removed after known installer exit or preparation failure. If configure might still run, preserve staging and return Unknown (60002). Exit 0 triggers Windows/Click-to-Run verification; 3010 requires manual restart. Completion does not assert activation/license validity. Caller temp is removed after known helper exit/UAC refusal; cancellation can leave it for TI cleanup.

UI cancellation detaches only the wait. The actual helper wait continues; a static gate blocks another deployment across adapter instances until helper exit. Elevated helper continues independently if Nexo closes. Across restarts, Office/process inventory provides the available guard; TI must inspect Unknown before retrying. UAC requires human authorization.

Offline checks use fake download/process runner, parse exact helper in Windows PowerShell 5.1, and run only fail-closed preexisting-directory/ACL branches against harmless user-owned fixtures. They never elevate/install Office. Real deployment remains untested. Pinned URL may need deliberate update. Preexisting elevated attacker/compromised Windows trust is outside this boundary.

Validation: dotnet run --project Nexo.Office.Checks -c Release; dotnet build Nexo.Support -c Release. Standard Nexo.Checks and Setup.Checks remain unchanged.

Sources: https://learn.microsoft.com/en-us/office/ltsc/2024/deploy and https://www.microsoft.com/en-us/download/details.aspx?id=49117.

After helper exit 0, Completed additionally requires HKLM 64-bit Click-to-Run evidence for ProPlus2024Volume/x64/pt-BR, a 16.0 LTSC 2024 build, StreamingFinished, and Word/Excel/PowerPoint files under the registered Program Files installation. Missing, mismatched, inaccessible or cancelled evidence returns Unknown. This proves observed installation, not activation or application behavior.

Preflight requires 8 GiB free on the Office volume (4 GB minimum plus staging/servicing margin), 256 MiB on the temp volume, and an active network interface. A link does not prove Microsoft CDN/proxy reachability; HTTP download failures explain network/proxy access and preparation errors distinguish permission or full disk. Office errors after elevation remain real installer errors. VM installation/validation required by issue #112 is still pending and this issue must not be closed from offline checks alone.
