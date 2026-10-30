# Contributing to RynthSuite

RynthCore is the hook layer: it injects into the client, reads the game, runs plugins and hosts their windows. RynthSuite is the plugins. RynthAi is one plugin among them, built on RynthCore the same way any other plugin is.

## Where a change goes

| It is... | It goes in |
|---|---|
| A hook into the client, game state, the plugin API, the window host, a service every plugin can use | **RynthCore** |
| A plugin's behaviour or its own screens | **That plugin** (`Plugins/RynthCore.Plugin.<Name>`), drawn by the plugin with the engine's ImGui |
| A feature for one game server | **That server's plugin**, in the **RynthServers** repository |

A shared plugin (RynthAi, RynthOracle, RynthInventory, RynthNav, ...) never names a server. When a server plugin needs a place in one of them, add a generic hook the server plugin fills (the ServerPack hooks, the server tab host, the storage and chat feeds) and keep the server's own code in the server plugin.

## Pull requests

- One purpose per pull request. A change that touches several layers comes as one pull request per layer, the RynthCore hook first.
- Plugin API numbers and plugin versions are assigned when a change is merged.
- Add or update tests where the project has them (`Tools/`, each plugin's `*.Tests`).
- Say in the description what you tested in game and on which server.
