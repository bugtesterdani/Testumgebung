# Optional external LabJack and MCC/RedLab runtimes

No vendor binaries, headers or SDK source are distributed with this integration. The application contains independently written API declarations. Users install the appropriate Windows-x64 vendor runtime separately.

LabJack's published default is MIT unless the particular package states otherwise. LJM includes additional third-party licenses (including Boost and gRPC on Windows). MCC permits redistribution of necessary UL runtime/hardware-driver files royalty-free but prohibits redistribution of development-enabling files. This project does not exercise that redistribution permission in this change.

See `docs/licensing/labjack-redlab.md`, the per-component external dependency inventory, and the licenses supplied with the installed vendor package. An exact redistributable package must be reviewed before any future bundling.

Sources:
- https://support.labjack.com/docs/what-is-labjack-software-licensed-under
- https://files.digilent.com/manuals/Mcculw_WebHelp/Users_Guide/Overview/Redistribute.htm
