# OrbitUS

OrbitUS is a mod I decided to start "developing" as a joke for my friends but they ended up wanting it so much I actually decided to make it.
It is for the game Orbitous on Steam: https://store.steampowered.com/app/3830300/Orbitous/

# Install Instructions

<h3><b>1. Install BepInEx</b></h3>
   <i>Since Orbitous is a Unity game, BepInEx is required to mod it.</i> <br> <br>
     To install BepInEx, download the latest release .zip from https://github.com/bepinex/bepinex (make sure to pick the right one for your operating system). Then extract the BepInEx folder to "C:\Program Files (x86)\Steam\steamapps\common\Orbitous" and run Orbitous once. <br> <img width="574" height="147" alt="Screenshot_20260912_225948" src="https://github.com/user-attachments/assets/ec22da32-8b15-4fef-949b-e5b667eed455" />


<h3><b>2. Install the mod</b></h3>
   To install the mod, download the latest release .zip and extract it to "C:\Program Files (x86)\Steam\steamapps\common\Orbitous\BepInEx\plugins"<br><img width="561" height="138" alt="Screenshot_20260912_232339" src="https://github.com/user-attachments/assets/39201ebe-7616-4cd3-92a0-57e801f99758" /> 
<h3><b>3. Install a reverse proxy program/VPN like Tailscale</b></h3>
   Currently, the mod has NO NAT traversal, so unless you have a public IP address and can port forward, you need to use a mesh VPN like Tailscale or a reverse proxy program like Playit.gg with premium <br> <br>
   By default the mod uses port 7777 for TCP Connections and port 7778 for UDP Connections, these, along with the IP address/Domain are changeable in "C:\Program Files (x86)\Steam\steamapps\common\Orbitous\BepInEx\config\Orbit-US.cfg" <br><br>

<h3><b>4. Start Playing</b></h3>
   To start the multiplayer server set the ip address in the config file to be the ip address, domain, or tailscale domain/ip of the host. When in-game, press the quick play button and have the HOST press F6, then have all clients quick play and press F7<br><br>

Currently, only the players are synced, so all game progress is client-sided and will not be reflected on other clients. This will be changed in the future! <br><br>
<b>Planned Features:<b> https://trello.com/b/NTNavEf7/orbitus-planned-features

<br> <br> <h2><b> AI DISCLAIMER: MOST IF NOT ALL CODE IN THIS MOD IS AI-GENERATED </b></h3>
