# Unity-OSCQuery
A seamless integration of OSCQuery in Unity 

# Installation

- In Unity Editor, open Windows > Package Manager
- Click the "+" icon in the top-left corner of the window, and choose "Add package from git URL..."
- Paste the url of this repo (include .git) : https://github.com/benkuper/Unity-OSCQuery.git
- That's it, you're set !


# Usage

Just add the script "OSCQuery" wherever you want and setup the inport port you wish to use for communication.

You can specify a specific object to be used as the root of the hierarchy to expose. Anything outside this object will not be shown in OSCQuery. Leave this field empty to expose all objects in the scene.

When in play mode, you can check that it's working by going to http://127.0.0.1:9010/(or whatever port you chose). 
You can watch the video to see a basic example.

# Demo
Click on the picture to see the youtube video
[![Click to see the youtube video](http://i3.ytimg.com/vi/pLfj06am8gU/maxresdefault.jpg)](https://www.youtube.com/watch?v=pLfj06am8gU)

# Kudos and mentions

This library embeds few other libraries to work :
- The very useful JSONObject library from Defective Studio (It will be removed from the package when this library becomes itself a Package and I can link to it as a package dependency) : https://assetstore.unity.com/packages/tools/input-management/json-object-710
- A modified version of UnityOSC from jorge garcia : https://github.com/jorgegarcia/UnityOSC
