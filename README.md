# Ledge Jump

A Dalamud plugin that jumps at the exact edge of platforms you record. It checks your position every frame, so it can be much more precise than a script.

## Install (custom repository)

1. In game, type `/xlsettings`, go to the **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste this link (replace the two names), then click **+** and **Save and Close**:

   `https://raw.githubusercontent.com/YOUR_GITHUB_NAME/LedgeJump/main/repo.json`

3. Type `/xlplugins`, search for **Ledge Jump**, and install it.

## Use

1. Walk to the very tip of the platform you want to jump from and stand still.
2. Type `/ledgejump add`.
3. Run at the platform edge. The plugin jumps when you reach the recorded spot.

Type `/ledgejump` to see all commands. The main one to tune is `/ledgejump radius <number>` (how far before the spot the jump fires; smaller = later, closer to the edge).
