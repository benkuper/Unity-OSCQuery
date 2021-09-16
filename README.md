# Unity-OSCQuery
A seamless integration of OSCQuery in Unity 

# Installation

## From Unity Editor

- In Unity Editor, open the Project Settings > Package Manager
- Click on the '+' icon to add a Scoped Registry. Then set it up like this :
  - Name : Ben Kuper
  - URL : https://package.openupm.com
  - Scope : com.benkuper

- When opening Windows > Package Manager, select "My Registry" in the list menu and you should be now able to install OSCQuery under the "Ben Kuper" group.

## From manifest.json

In your Unity project folder, open Packages/manifest.json and edit (or add it not already there) the scopedRegistry section. You can put that anywhere in the JSON file at the root level of the tree structure. More info on the manifest.json file here : https://docs.unity3d.com/2019.3/Documentation/Manual/upm-manifestPrj.html

```
"scopedRegistries": [
    {
      "name": "Ben Kuper",
      "url": "https://package.openupm.com",
      "scopes": [
        "com.benkuper"
      ]
    }
  ]
```

# Usage

Just add the script "OSCQuery" wherever you want and setup the input port you wish to use for communication.

You can specify a specific object to be used as the root of the hierarchy to expose. Anything outside this object will not be shown in OSCQuery. Leave this field empty to expose all objects in the scene.

When in play mode, you can check that it's working by going to http://127.0.0.1:9010/ (or whatever port you chose). 
You can watch the video to see a basic example.

# Demo

You can check out this example project using OSCQuery to control simple cubes : https://github.com/benkuper/Unity-OSCQuery-Example

Click on the picture to see the youtube video
[![Click to see the youtube video](http://i3.ytimg.com/vi/pLfj06am8gU/maxresdefault.jpg)](https://www.youtube.com/watch?v=pLfj06am8gU)

# Kudos and mentions

This library embeds few other libraries to work :
- The very useful JSONObject library from Defective Studio (It will be removed from the package when this library becomes itself a Package and I can link to it as a package dependency) : https://assetstore.unity.com/packages/tools/input-management/json-object-710
- A modified version of UnityOSC from jorge garcia : https://github.com/jorgegarcia/UnityOSC
