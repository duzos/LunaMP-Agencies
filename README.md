<p align="center">
    <img src="../master/External/logo.png" alt="Luna multiplayer logo"/>
    <a href="https://www.youtube.com/watch?v=rmJL_c-EJK8"><img src="https://img.youtube.com/vi/rmJL_c-EJK8/0.jpg" alt="Video" height="187" width="250"/></a>    
    <a href="https://www.youtube.com/watch?v=gf6xyLnpnoM"><img src="https://img.youtube.com/vi/gf6xyLnpnoM/0.jpg" alt="Video" height="187" width="250"/></a>
</p>

<p align="center">
    <a href="https://paypal.me/gavazquez"><img src="https://img.shields.io/badge/paypal-donate-yellow.svg?style=flat&logo=paypal" alt="PayPal"/></a>
    <a href="https://discord.gg/wKVMhWQ"><img src="https://img.shields.io/discord/378456662392045571.svg?style=flat&logo=discord&label=discord" alt="Chat on discord"/></a>
    <a href="../../releases"><img src="https://img.shields.io/github/release/lunamultiplayer/lunamultiplayer.svg?style=flat&logo=github&logoColor=white" alt="Latest release" /></a>
    <a href="../../releases"><img src="https://img.shields.io/github/downloads/lunamultiplayer/lunamultiplayer/total.svg?style=flat&logo=github&logoColor=white" alt="Total downloads" /></a>
</p>

<p align="center">
    <a href="https://forum.kerbalspaceprogram.com/index.php?/topic/168271-131-luna-multiplayer-lmp-alpha"><img src="https://img.shields.io/badge/KSP%20Forum-Post-4265f4.svg?style=flat" alt="KSP forum post"/></a>
    <a href="https://github.com/LunaMultiplayer/LunaMultiplayerUpdater"><img src="https://img.shields.io/badge/Automatic-Updater-4265f4.svg?style=flat" alt="Latest build updater"/></a>
</p>

---

<p align="center">
  <a href="../../releases/latest"><img src="../master/External/downloadIcon.png" alt="Download" height="85" width="300"/></a>
  <a href="../../wiki"><img src="../master/External/documentationIcon.png" alt="Documentation" height="85" width="353"/></a>
</p>

---

# Luna Multiplayer Mod (LMP)

*Multiplayer mod for [Kerbal Space Program (KSP)](https://kerbalspaceprogram.com)*

### Main features:

- [x] Clean and optimized code, based on systems and windows which makes it easier to read and modify.
- [x] Multi threaded.
- [x] [NTP](https://en.wikipedia.org/wiki/Network_Time_Protocol) protocol to sync the time between clients and the server.
- [x] [UDP](https://en.wikipedia.org/wiki/User_Datagram_Protocol) based using the [Lidgren](https://github.com/lidgren/lidgren-network-gen3) library for reliable UDP message handling.
- [x] [Interpolation](http://www.gabrielgambetta.com/entity-interpolation.html) so the vessels won't jump when there are bad network conditions.
- [x] Multilanguage.
- [x] [Nat-punchthrough](../../wiki/Master-server) feature so a server doesn't need to open ports on it's router.
- [x] [IPv6](https://en.wikipedia.org/wiki/IPv6) support for client<->server connections, allowing connection setup even behind symmetric IPv4 NAT
- [x] Servers displayed within the mod.
- [x] Settings saved as XML.
- [x] [UPnP](https://en.wikipedia.org/wiki/Universal_Plug_and_Play) support for servers and [master servers](../../wiki/Master-server)
- [x] Better creation of network messages so they are easier to modify and serialize.
- [x] Every network message is cached in order to reduce the garbage collector spikes.
- [x] Based on tasks instead of threads.
- [x] Supports career and science modes (funds, science, strategies, etc are shared between all players).
- [x] Cached [QuickLZ](http://www.quicklz.com) for fast compression without generating garbage.
- [x] Support for groups/companies inside career and science modes (this fork — see **Agencies** below).

Please check the [wiki](../../wiki) to see how to [install](../../wiki/How-to-install-LMP), [run](../../wiki/How-to-play-with-LMP), [build](../../wiki/How-to-compile-LMP) or [debug](../../wiki/Debugging-in-Visual-studio) LMP among other things

---

### Fork feature: Agencies

This fork adds **agency-based career progression**. Instead of one shared career pool,
the server tracks multiple agencies (groups/companies) and every authenticated player
belongs to exactly one of them. Each agency owns its own:

- Funds, Science, and Reputation pools
- Tech-tree unlocks and purchased parts
- Chat channel (`Global` and `Agency` scopes, toggled in the chat window)

**Contracts.** Every contract the server generates is visible to every player in Mission
Control — you all browse the same list. The **first agency to accept a given contract becomes
its owner**; it's locked to that agency from then on, and only the owning agency's members can
progress or complete it. Rewards (funds, science, reputation) and penalties go only to the
owning agency. Other agencies see the contract flagged as owned and cannot re-accept it.

**Science across agencies.** Each agency's *total science pool* is fully private — spending
science on tech nodes only deducts from your own agency. However, per-subject experiment caps
(e.g. `temperature@KerbinSrfLandedLaunchPad`) are tracked server-wide: once any agency has
maxed out a specific experiment/biome combo, other agencies running the same experiment get
reduced science from it. This is **intentional** — it preserves each agency's career
independence while still rewarding the "first to do it" agency with the lion's share of any
given experiment, rather than letting every agency independently grind the same handful of
biome checks for infinite science.

**No-agency fallback.** Every player always has an agency. If they haven't created or joined one
explicitly, the server auto-creates a private `Solo-<name>` agency on handshake so the "career is
per-agency" invariant never breaks. Solo agencies are hidden from the Browse UI and auto-deleted
when their sole member disconnects permanently.

**Migration.** When the server first boots with pre-existing career state in
`Universe/Scenarios/`, a one-off migration creates a *Default Agency* that inherits all of it
(funds, science, reputation, tech tree, contracts). No data is lost on upgrade.

**UI.** An Agency toolbar button opens a window with three tabs:

- *Mine* — your agency's details, resources, members, owner-only actions (rename / kick /
  transfer ownership), and inter-agency resource transfer (send funds or science to another agency).
- *Browse* — every public agency on the server with a "Request to join" button.
- *Create* — name and submit a new agency; you automatically become its owner.

The in-game admin window (press Admin, enter admin password) gets a new **Agencies** tab with:

- List / inspect / rename / force-delete agencies
- Move any player into any agency
- Force-set any player as an agency's owner
- Cheat controls: set funds / science / reputation, force-unlock a tech node,
  force-complete or cancel a specific contract

**Console commands.** For ops without an in-game client the server console exposes the same
operations:

```
/listagencies                              List all agencies with summary info
/agencyinfo <name|id>                      Dump full info for one agency
/createagency <name> [ownerUid] [display]  Create an agency from the console
/deleteagency <name|id> [--force]          Delete an agency
/renameagency <name|id> <newName>
/moveplayeragency <playerUid> <name|id>
/transferagencyowner <name|id> <newOwnerUid>
/setagencyfunds <name|id> <value>          Cheat funds
/setagencyscience <name|id> <value>        Cheat science
/setagencyrep <name|id> <value>            Cheat reputation
/unlockagencytech <name|id> <techNodeId>   Force-unlock a tech node
/completeagencycontract <name|id> <guid>
/cancelagencycontract <name|id> <guid>
```

**Agency-change UX.** Creating an agency, being approved into one, being kicked, leaving,
or being admin-moved all force a clean reconnect — the client is disconnected with a
human-readable reason ("You joined 'Kerbin Dynamics'. Reconnecting..."), re-authenticates,
and loads the new agency's career state on re-entry. This is necessary because KSP's stock
scenario modules cannot cleanly swap career state at runtime (the tech tree in particular
has no "un-unlock" API).

**Persistence.** Each agency lives in
`Universe/Agencies/<guid>/meta.txt` (name, owner, members, headline funds/sci/rep) plus a
`Universe/Agencies/<guid>/Scenarios/` folder containing the agency's own copy of Funding,
Reputation, ResearchAndDevelopment, ContractSystem, and related career modules. Non-career
scenarios (DeployedScience, CommNetScenario, etc.) remain in the global `Universe/Scenarios/`
folder and are shared.

---
### Troubleshooting:

Please visit [this page](../../wiki/Troubleshooting) in the wiki to solve the most common issues with LMP 
[![Analytics](https://ga-beacon.appspot.com/UA-118326748-1/Home?pixel&useReferer)](https://github.com/igrigorik/ga-beacon)

---
### Contributing:

Consider [donating through paypal](https://paypal.me/gavazquez) if you like this project. 
It will encourage us to do future releases, fix bugs and add new features :star:

Please write the code as you were going to leave it, return after 1 year and you'd have to understand what you wrote.  
It's **very** important that the code is clean and documented so in case someone leaves, another programmer could take and maintain it. Bear in mind that **nobody** likes to take a project where it's code looks like a dumpster.

There's also a test project in case you want to add tests to your code.

---
### Servers:

You can check [how many servers are up](../../wiki/Master-server-status) and running either in [Release](../../wiki/How-to-get-the-latest-version-of-LMP) or in [Nightly](../../wiki/How-to-get-nightly-builds) versions through our [master servers](../../wiki/Master-server)

| Master server | Release | Nightly |
| ------------  | ------- |-------- |
[Dagger](https://github.com/gavazquez) | [![Release servers](https://img.shields.io/website-up-down-brightgreen-red/http/servers.lunamultiplayer.com:8701.svg?label=status)](http://servers.lunamultiplayer.com:8701) | [![Nightly servers](https://img.shields.io/website-up-down-brightgreen-red/http/servers.lunamultiplayer.com:8751.svg?label=status)](http://servers.lunamultiplayer.com:8751) |
DasSkelett | [![Release servers](https://img.shields.io/website-up-down-brightgreen-red/http/ms.lmp.dasskelett.dev.svg?label=status)](http://ms.lmp.dasskelett.dev) | [![Nightly servers](https://img.shields.io/website-up-down-brightgreen-red/http/ms.lmp.dasskelett.dev.svg?label=status)](http://ms.lmp.dasskelett.dev) |
Nightshade | [![Release servers](https://img.shields.io/website-up-down-brightgreen-red/http/lmp.nightshade.fun:8701.svg?label=status)](http://lmp.nightshade.fun:8701) | [![Nightly servers](https://img.shields.io/website-up-down-brightgreen-red/http/lmp.nightshade.fun:8751.svg?label=status)](http://lmp.nightshade.fun:8751) |

---
### Status:

|   Branch   |   Build  |   Tests  |  Last commit  |   Activity    |    Commits    |
| ---------- | -------- | -------- | ------------- | ------------- | ------------- |
| **master** |[![AppVeyor](https://img.shields.io/appveyor/ci/gavazquez/lunamultiplayer/master.svg?style=flat&logo=appveyor)](https://ci.appveyor.com/project/gavazquez/lunamultiplayer/branch/master) | [![AppVeyor Tests](https://img.shields.io/appveyor/tests/gavazquez/lunamultiplayer/master.svg?style=flat&logo=appveyor)](https://ci.appveyor.com/project/gavazquez/lunamultiplayer/branch/master/tests) | [![Last commit](https://img.shields.io/github/last-commit/lunamultiplayer/lunamultiplayer/master.svg?style=flat&logo=github&logoColor=white)](../../commits/master) | [![Commit activity](https://img.shields.io/github/commit-activity/y/lunamultiplayer/lunamultiplayer.svg?style=flat&logo=github&logoColor=white)](../../commits/master) | [![Commits since release](https://img.shields.io/github/commits-since/lunamultiplayer/lunamultiplayer/latest.svg?style=flat&logo=github&logoColor=white)](../../commits/master)

<p align="center">
    <a href="https://ci.appveyor.com/project/gavazquez/lunamultiplayer/history"><img src="https://buildstats.info/appveyor/chart/gavazquez/lunamultiplayer?buildCount=100" alt="Build history"/></a>
</p>

---

<p align="center">
  <a href="mailto:gavazquez@gmail.com"><img src="https://img.shields.io/badge/email-gavazquez@gmail.com-blue.svg?style=flat" alt="Email: gavazquez@gmail.com" /></a>
  <a href="./LICENSE"><img src="https://img.shields.io/github/license/lunamultiplayer/LunaMultiPlayer.svg" alt="License" /></a>
</p>
