using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using UnityEngine;
using OSCQuery.UnityOSC;
using Mono.Zeroconf.Providers.Bonjour;
using WebSocketSharp.Server;
using UnityEngine.VFX;
using UnityEngine.Rendering;

namespace OSCQuery
{
    public class CompInfo
    {
        public CompInfo(Component comp, FieldInfo info)
        {
            infoType = InfoType.Field;
            fieldInfo = info;
            type = fieldInfo.FieldType;
            this.comp = comp;
        }

        public CompInfo(Component comp, PropertyInfo info)
        {
            infoType = InfoType.Property;
            propInfo = info;
            type = propInfo.PropertyType;
            this.comp = comp;
        }

        public CompInfo(Component comp, MethodInfo info)
        {
            infoType = InfoType.Method;
            methodInfo = info;
            this.comp = comp;
        }

        public CompInfo(VisualEffect comp, string prop, Type type)
        {
            infoType = InfoType.VFX;
            genericInfo = new GenericInfo(type, prop);
            this.type = type;
            this.comp = comp;
        }

        public Component comp;

        public enum InfoType { Property, Field, Method, VFX, Material };
        public InfoType infoType;

        public Type type;
        public FieldInfo fieldInfo;
        public PropertyInfo propInfo;
        public MethodInfo methodInfo;

        public struct GenericInfo
        {
            public GenericInfo(Type type, string name)
            {
                this.type = type;
                this.name = name;
            }
            public Type type;
            public string name;
        };

        public GenericInfo genericInfo;
    }

    [ExecuteInEditMode]
    public class OSCQuery : MonoBehaviour
    {
        [Header("Network settings")]
        public int localPort = 9010;
        public string serverName = "Unity-OSCQuery";

        [Header("Setup & Filters")]
        public GameObject rootObject;
        public enum FilterMode { All, Include, Exclude }
        public FilterMode objectFilterMode = FilterMode.Exclude;
        public List<GameObject> filteredObjects;
        public FilterMode componentFilterMode = FilterMode.Exclude;
        public List<String> filteredComponentNames;
        public bool excludeInternalUnityParams;
        String[] internalUnityParamsNames = { "name", "tag", "useGUILayout", "runInEditMode", "enabled", "hideFlags" };
        String[] internalUnityTransformNames = { "localEulerAngles", "right", "up", "forward", "hasChanged", "hierarchyCapacity" };
        String[] acceptedParamTypes = { "System.String", "System.Char", "System.Boolean", "System.Int32", "System.Int64", "System.Int16", "System.UInt16", "System.Byte", "System.SByte", "System.Double", "System.Single", "UnityEngine.Vector2", "UnityEngine.Vector3", "UnityEngine.Quaternion", "UnityEngine.Color" };

        HttpServer httpServer;

        JSONObject queryData;
        bool dataIsReady;

        OSCReceiver receiver;

        Dictionary<string, CompInfo> compInfoMap;

        Dictionary<WSQuery, List<string>> propQueryMap;
        Dictionary<string, object> propQueryPreviousValues;
        bool propQueryMapLock;

        RegisterService zeroconfService;
        RegisterService oscService;

        void Awake()
        {
        }

        private void OnEnable()
        {
            if (propQueryMap == null) propQueryMap = new Dictionary<WSQuery, List<string>>();
            if (propQueryPreviousValues == null) propQueryPreviousValues = new Dictionary<string, object>();

            if (filteredComponentNames.Count == 0)
            {
                filteredComponentNames.Add("MeshRenderer");
                filteredComponentNames.Add("MeshFilter");
                filteredComponentNames.Add("BoxCollider");
                filteredComponentNames.Add("MeshCollider");
            }

            if (Application.isPlaying)
            {
                httpServer = new HttpServer(localPort);
                httpServer.OnGet += handleHTTPRequest;

                httpServer.Start();
                if (httpServer.IsListening) Debug.Log("OSCQuery Server started on port " + localPort);
                else Debug.LogWarning("OSCQuery could not start on port " + localPort);

                httpServer.AddWebSocketService("/", createWSQuery);

                receiver = new OSCReceiver();
                receiver.Open(localPort);

                oscService = new RegisterService();
                oscService.Name = serverName;
                oscService.RegType = "_osc._udp";
                oscService.ReplyDomain = "local.";
                oscService.UPort = (ushort)localPort;
                oscService.Register();

                zeroconfService = new RegisterService();
                zeroconfService.Name = serverName;
                zeroconfService.RegType = "_oscjson._tcp";
                zeroconfService.ReplyDomain = "local.";
                zeroconfService.UPort = (ushort)localPort;
                zeroconfService.Register();
            }

            rebuildDataTree();
        }


        private void OnDisable()
        {
            httpServer?.Stop();
            receiver?.Close();
            zeroconfService?.Dispose();
            oscService?.Dispose();
        }

        // Update is called once per frame
        void Update()
        {
            if (Application.isPlaying)
            {
                if (!dataIsReady)
                {
                    rebuildDataTree();
                    dataIsReady = true;
                }

                ProcessIncomingMessages();
            }

            //while(propQueryMapLock)
            //{
            //wait
            //}
            propQueryMapLock = true;
            foreach (KeyValuePair<WSQuery, List<string>> qprops in propQueryMap)
            {
                foreach (string s in qprops.Value) sendFeedback(s, qprops.Key);
            }
            propQueryMapLock = false;

        }

        private void handleHTTPRequest(object sender, HttpRequestEventArgs e)
        {
            var req = e.Request;
            var response = e.Response;

            JSONObject responseData = new JSONObject();

            if (req.RawUrl.Contains("HOST_INFO"))
            {
                JSONObject extensions = new JSONObject();
                extensions.SetField("ACCESS", true);
                extensions.SetField("CLIPMODE", false);
                extensions.SetField("CRITICAL", false);
                extensions.SetField("RANGE", true);
                extensions.SetField("TAGS", false);
                extensions.SetField("TYPE", true);
                extensions.SetField("UNIT", false);
                extensions.SetField("VALUE", true);
                extensions.SetField("LISTEN", true);

                responseData.SetField("EXTENSIONS", extensions);
                responseData.SetField("NAME", serverName);
                responseData.SetField("OSC_PORT", localPort);
                responseData.SetField("OSC_TRANSPORT", "UDP");
            }
            else
            {
                dataIsReady = false;
                while (!dataIsReady) { /* wait until data is rebuild */ }
                responseData = queryData;
            }

            byte[] buffer = System.Text.Encoding.UTF8.GetBytes(responseData.ToString());
            response.ContentType = "application/json";
            response.ContentLength64 = buffer.Length;
            response.Close(buffer, true);

        }

        WSQuery createWSQuery()
        {
            WSQuery q = new WSQuery();
            q.messageReceived += wsMessageReceived;
            q.dataReceived += wsDataReceived;
            q.socketOpened += wsSocketOpened;
            q.socketClosed += wsSocketClosed;
            q.socketError += wsSocketError;
            return q;
        }

        private void wsSocketOpened(WSQuery query)
        {
            propQueryMap.Add(query, new List<string>());
        }

        private void wsSocketClosed(WSQuery query)
        {
            propQueryMap.Remove(query);

        }

        private void wsSocketError(WSQuery query)
        {
            propQueryMap.Remove(query);
        }

        private void wsMessageReceived(WSQuery query, string message)
        {
            Debug.Log("Message received " + message);
            JSONObject o = new JSONObject(message);
            if (o.IsObject)
            {
                String command = o["COMMAND"].str;
                String data = o["DATA"].str;
                Debug.Log("Received Command " + command + " > " + data);

                while (propQueryMapLock)
                {
                    //wait
                }
                propQueryMapLock = true;

                if (command == "LISTEN")
                {
                    propQueryMap[query].Add(data);
                }
                else if (command == "IGNORE")
                {
                    propQueryMap[query].Remove(data);
                }

                propQueryMapLock = false;
            }

        }

        private void wsDataReceived(WSQuery query, byte[] data)
        {
            Debug.Log("Data received " + data.Length + " bytes");
        }

        void rebuildDataTree()
        {
            compInfoMap = new Dictionary<string, CompInfo>();

            if (rootObject != null) queryData = getObjectData(rootObject, ""); 
            else
            {
                queryData = new JSONObject("root");
                queryData.SetField("ACCESS", 0);
                if (!UnityEngine.SceneManagement.SceneManager.GetActiveScene().isLoaded) return;
                GameObject[] rootObjects = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();

                JSONObject co = new JSONObject();
                foreach (GameObject go in rootObjects)
                {
                    if (!checkFilteredObject(go)) continue;
                    string goName = SanitizeName(go.name);
                    co.SetField(goName, getObjectData(go, "/" + goName));
                }

                queryData.SetField("CONTENTS", co);
            }
        }

        JSONObject getObjectData(GameObject go, string baseAddress = "")
        {
            JSONObject o = new JSONObject();
            o.SetField("ACCESS", 0);
            JSONObject co = new JSONObject();
            for (int i = 0; i < go.transform.childCount; i++)
            {
                GameObject cgo = go.transform.GetChild(i).gameObject;
                if (!checkFilteredObject(cgo)) continue;
                string cgoName = SanitizeName(cgo.name);
                co.SetField(cgoName, getObjectData(cgo, baseAddress + "/" + cgoName));
            }

            Component[] comps = go.GetComponents<Component>();

            foreach (Component comp in comps)
            {
                int dotIndex = comp.GetType().ToString().LastIndexOf(".");
                string compType = comp.GetType().ToString().Substring(Mathf.Max(dotIndex + 1, 0));

                //Debug.Log(go.name+" > Comp : " + compType);
                if (!checkFilteredComp(compType)) continue;

                string compAddress = baseAddress + "/" + compType;

                JSONObject cco = new JSONObject();
                cco.SetField("ACCESS", 0);

                JSONObject ccco = new JSONObject();

                FieldInfo[] fields = comp.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public);

                foreach (FieldInfo info in fields)
                {
                    RangeAttribute rangeAttribute = info.GetCustomAttribute<RangeAttribute>();

                    //Debug.Log(go.name+" > Info field type : " +info.FieldType.ToString() +" /" +compType);

                    JSONObject io = getPropObject(info.FieldType, info.GetValue(comp), rangeAttribute, info.Name == "mainColor");

                    if (io != null)
                    {
                        string ioName = SanitizeName(info.Name);
                        string fullPath = compAddress + "/" + ioName;
                        io.SetField("FULL_PATH", fullPath);
                        ccco.SetField(ioName, io);
                        compInfoMap.Add(fullPath, new CompInfo(comp, info));
                    }
                }

                PropertyInfo[] props = comp.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.SetProperty);
                foreach (PropertyInfo info in props)
                {
                    if (!info.CanWrite) continue;
                    string propType = info.PropertyType.ToString();
                    if (!acceptedParamTypes.Contains(propType)) continue;//
                    //if (propType == "UnityEngine.Component") continue; //fix deprecation error
                    //if (propType == "UnityEngine.GameObject") continue; //fix deprecation error
                    //if (propType == "UnityEngine.Matrix4x4") continue; //fix deprecation error
                    //if (propType == "UnityEngine.Transform") continue; //fix deprecation error
                    //if (propType == "UnityEngine.Mesh") continue; //fix deprecation error
                    if (excludeInternalUnityParams)
                    {
                        if (internalUnityParamsNames.Contains(info.Name)) continue;
                        if (compType == "Transform" && internalUnityTransformNames.Contains(info.Name)) continue;
                    }


                    RangeAttribute rangeAttribute = info.GetCustomAttribute<RangeAttribute>();
                    JSONObject io = getPropObject(info.PropertyType, info.GetValue(comp), rangeAttribute, false);

                    if (io != null)
                    {
                        string ioName = SanitizeName(info.Name);
                        string fullPath = compAddress + "/" + ioName;
                        io.SetField("FULL_PATH", fullPath);
                        ccco.SetField(SanitizeName(info.Name), io);
                        compInfoMap.Add(fullPath, new CompInfo(comp, info));
                    }
                }
                
                if (compType == "VisualEffect")
                {
                    VisualEffect vfx = comp as VisualEffect;
                    List<VFXExposedProperty> vfxProps = new List<VFXExposedProperty>();
                    vfx.visualEffectAsset.GetExposedProperties(vfxProps);
                    foreach (var p in vfxProps)
                    {
                        //Debug.Log("Here " + p.name+" / "+p.type.ToString());
                        JSONObject io = getPropObject(p.type, getVFXPropValue(vfx, p.type, p.name));

                        if (io != null)
                        {
                            string sName = SanitizeName(p.name);
                            string fullPath = compAddress + "/" + sName;
                            io.SetField("FULL_PATH", fullPath);
                            ccco.SetField(SanitizeName(sName), io);
                            compInfoMap.Add(fullPath, new CompInfo(comp as VisualEffect, p.name, p.type));
                        }
                    }

                }
                else if (compType == "Volume")
                {
                   //Volume v = comp as Volume; 

                }
                else if (compType != "Transform") //Avoid methods of internal components
                {
                    MethodInfo[] methods = comp.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
                    foreach (MethodInfo info in methods)
                    {
                        if (info.IsSpecialName && (info.Name.StartsWith("set_") || info.Name.StartsWith("get_"))) continue; //do not care for accessors

                        ParameterInfo[] paramInfos = info.GetParameters();
                        bool requiresArguments = false;
                        foreach (ParameterInfo paramInfo in paramInfos)
                        {
                            if (!paramInfo.HasDefaultValue)
                            {
                                requiresArguments = true;
                                break;
                            }
                        }

                        if (!requiresArguments)
                        {
                            JSONObject mo = new JSONObject();
                            mo.SetField("ACCESS", 3);
                            String ioName = SanitizeName(info.Name);
                            string fullPath = compAddress + "/" + ioName;
                            mo.SetField("TYPE", "N");
                            mo.SetField("FULL_PATH", fullPath);
                            ccco.SetField(ioName, mo);
                            compInfoMap.Add(fullPath, new CompInfo(comp, info));

                            //Debug.Log("Added method : " + ioName);
                        }
                        else
                        {
                            //  Debug.Log("Method : " + info.Name + " requires arguments, not exposing");
                        }
                    }
                }

                cco.SetField("CONTENTS", ccco);
                co.SetField(SanitizeName(compType), cco);
            }

            o.SetField("CONTENTS", co);

            return o;
        }

        JSONObject getPropObject(Type type, object value, RangeAttribute range = null, bool debug = false)
        {
            JSONObject po = new JSONObject();
            po.SetField("ACCESS", 3);

            JSONObject vo = new JSONObject();
            JSONObject ro = new JSONObject();
            JSONObject ro0 = new JSONObject();
            if (range != null)
            {
                ro0.SetField("MIN", range.min);
                ro0.SetField("MAX", range.max);
            }

            string typeString = type.ToString();
            string poType = "";


            switch (typeString)
            {
                case "System.String":
                case "System.Char":
                    vo.Add(value.ToString());
                    poType = "s";
                    break;

                case "System.Boolean":
                    vo.Add((bool)value);
                    poType = "T";
                    break;

                case "System.Int32":
                case "System.Int64":
                case "System.Int16":
                case "System.UInt16":
                case "System.Byte":
                case "System.SByte":
                    {
                        //add range
                        vo.Add((int)value);
                        poType = "i";
                    }
                    break;

                case "System.Double":
                case "System.Single":
                    {
                        //add range
                        vo.Add((float)value);
                        poType = "f";
                    }
                    break;

                case "UnityEngine.Vector2":
                    {
                        Vector2 v = (Vector2)value;
                        vo.Add(v.x);
                        vo.Add(v.y);
                        poType = "ff";
                    }
                    break;

                case "UnityEngine.Vector3":
                    {
                        Vector3 v = (Vector3)value;
                        vo.Add(v.x);
                        vo.Add(v.y);
                        vo.Add(v.z);
                        poType = "fff";
                    }
                    break;

                case "UnityEngine.Quaternion":
                    {
                        Vector3 v = ((Quaternion)value).eulerAngles;
                        vo.Add(v.x);
                        vo.Add(v.y);
                        vo.Add(v.z);
                        poType = "fff";
                    }
                    break;

                case "UnityEngine.Color":
                    {
                        Color c = (Color)value;
                        vo.Add(ColorUtility.ToHtmlStringRGBA(c));
                        poType = "r";
                    }
                    break;

                case "UnityEngine.Vector4":
                    {
                        Color c = (Color)(Vector4)value;
                        vo.Add(ColorUtility.ToHtmlStringRGBA(c));
                        poType = "r";
                    }
                    break;

                /*
            case "UnityEngine.Material":
                {
                    Material m = (Material)value;
                    if(m != null)
                    {
                        int numProps = m.shader.GetPropertyCount();
                        for(int i=0;i<numProps;i++)
                        {
                            m.shader.GetPropertyAttributes(i);
                        }
                    }
                }
                break;
                */
                default:
                    if (type.IsEnum)
                    {
                        JSONObject enumO = new JSONObject();

                        FieldInfo[] fields = type.GetFields();

                        foreach (var field in fields)
                        {
                            if (field.Name.Equals("value__")) continue;
                            enumO.Add(field.Name);
                        }
                        ro0.SetField("VALS", enumO);
                        vo.Add(value.ToString());
                        poType = "s";

                    }
                    else
                    {
                        // Debug.LogWarning("Field type not supported " + typeString);
                        return null;
                    }
                    break;

            }

            if (ro0 != null) ro.Add(ro0);
            po.SetField("VALUE", vo);
            po.SetField("TYPE", poType);
            po.SetField("RANGE", ro);

            return po;
        }


        string SanitizeName(string niceName)
        {
            return niceName.Replace(" ", "-").Replace("(", "").Replace(")", "");
        }

        bool checkFilteredObject(GameObject go)
        {
            if (go == null) return false;

            return objectFilterMode == FilterMode.All
                || (objectFilterMode == FilterMode.Include && filteredObjects.Contains(go))
                || (objectFilterMode == FilterMode.Exclude && !filteredObjects.Contains(go));
        }

        bool checkFilteredComp(String typeString)
        {
            return componentFilterMode == FilterMode.All
              || (componentFilterMode == FilterMode.Include && filteredComponentNames.Contains(typeString))
              || (componentFilterMode == FilterMode.Exclude && !filteredComponentNames.Contains(typeString));
        }

        void ProcessIncomingMessages()
        {
            while (receiver != null && receiver.hasWaitingMessages())
            {
                OSCMessage msg = receiver.getNextMessage();

                string args = "";
                msg.Data.ForEach((arg) => args += arg.ToString() + ", ");

                //Debug.Log("info received : " + msg.Address);
                if (compInfoMap.ContainsKey(msg.Address))
                {
                    CompInfo info = compInfoMap[msg.Address];
                    object data = null;

                    if (info == null)
                    {
                        //Debug.LogWarning("Address not found : " + msg.Address);
                        continue;
                    }


                    if (info.infoType == CompInfo.InfoType.Method)
                    {
                        int numParams = info.methodInfo.GetParameters().Length;
                        info.methodInfo.Invoke(info.comp, Enumerable.Repeat(Type.Missing, numParams).ToArray());
                        continue;
                    }

                    string typeString = info.type.ToString();

                    switch (typeString)
                    {
                        case "System.String":
                        case "System.Char":
                            data = msg.Data[0].ToString();
                            break;

                        case "System.Boolean":
                            data = ((int)msg.Data[0] == 1);
                            break;

                        case "System.Int32":
                        case "System.Int64":
                        case "System.UInt32":
                        case "System.Int16":
                        case "System.UInt16":
                        case "System.Byte":
                        case "System.SByte":
                            data = (int)msg.Data[0];
                            break;

                        case "System.Double":
                        case "System.Single":
                            data = getFloatArg(msg.Data[0]);
                            break;

                        case "UnityEngine.Vector2":
                            data = new Vector2(getFloatArg(msg.Data[0]), getFloatArg(msg.Data[1]));
                            break;

                        case "UnityEngine.Vector3":
                            data = new Vector3(getFloatArg(msg.Data[0]), getFloatArg(msg.Data[1]), getFloatArg(msg.Data[2]));

                            break;

                        case "UnityEngine.Quaternion":
                            data = Quaternion.Euler(new Vector3(getFloatArg(msg.Data[0]), getFloatArg(msg.Data[1]), getFloatArg(msg.Data[2])));
                            break;

                        case "UnityEngine.Color":
                        case "UnityEngine.Vector4":
                            {
                                if (msg.Data.Count == 1) data = (Color)msg.Data[0];
                                else if (msg.Data.Count >= 3) data = new Color(getFloatArg(msg.Data[0]), getFloatArg(msg.Data[1]), getFloatArg(msg.Data[2]), msg.Data.Count > 3 ? getFloatArg(msg.Data[2] ): 1.0f);
                            }
                            break;


                        default:
                            if (info.type.IsEnum)
                            {
                                JSONObject enumO = new JSONObject();

                                FieldInfo[] fields = info.type.GetFields();

                                foreach (var field in fields)
                                {
                                    if (field.Name.Equals("value__")) continue;
                                    if (field.Name == msg.Data[0].ToString())
                                    {
                                        //Debug.Log("Found enum " + field.Name + " > " + field.GetRawConstantValue());
                                        data = field.GetRawConstantValue();
                                    }
                                }
                            }
                            break;
                    }

                    if (data != null)
                    {
                        switch (info.infoType)
                        {
                            case CompInfo.InfoType.Field:
                                info.fieldInfo.SetValue(info.comp, data);
                                break;

                            case CompInfo.InfoType.Property:
                                info.propInfo.SetValue(info.comp, data);
                                break;

                            case CompInfo.InfoType.VFX:
                                {
                                    int dotIndex = info.comp.GetType().ToString().LastIndexOf(".");
                                    string compType = info.comp.GetType().ToString().Substring(Mathf.Max(dotIndex + 1, 0));
                                    setVFXPropValue((info.comp as VisualEffect), info.genericInfo.type, info.genericInfo.name, data);
                                }
                                break;

                            case CompInfo.InfoType.Material:
                                /* {
                                     int dotIndex = info.comp.GetType().ToString().LastIndexOf(".");
                                     string compType = info.comp.GetType().ToString().Substring(Mathf.Max(dotIndex + 1, 0));
                                     setMaterialPropValue((info.comp as VisualEffect), info.genericInfo.type, info.genericInfo.name, data);
                                 }*/
                                break;
                        }
                    }
                    else
                    {
                        Debug.LogWarning("Type not handled : " + typeString + ", address : " + msg.Address);
                    }
                }
                else
                {
                    Debug.LogWarning("Property not found for address : " + msg.Address);
                }
            }
        }


        void sendFeedback(string address, WSQuery query)
        {
            if (!compInfoMap.ContainsKey(address))
            {
                Debug.Log("Address " + address + " is not registered, skipping feedback");
                return;
            }

            CompInfo info = compInfoMap[address];

            object oldData = null;
            propQueryPreviousValues.TryGetValue(address, out oldData);

            object data = null;
            if (info.propInfo != null) data = info.propInfo.GetValue(info.comp);
            else if (info.fieldInfo != null) data = info.fieldInfo.GetValue(info.comp);
            else if (info.infoType == CompInfo.InfoType.VFX)
            {
                data = getVFXPropValue(info.comp as VisualEffect, info.genericInfo.type, info.genericInfo.name);
            }

            if (data == null) return;

            if (!propQueryPreviousValues.ContainsKey(address)) propQueryPreviousValues.Add(address, data);
            else propQueryPreviousValues[address] = data;

            OSCMessage m = new OSCMessage(address);

            string dataType = data.GetType().Name;
            switch (dataType)
            {
                case "Boolean":
                    {
                        bool val = (bool)data;
                        if (oldData != null && val == (bool)oldData) return;
                        m.Append(val ? 1 : 0);
                    }
                    break;

                case "Vector2":
                    {
                        Vector2 v = (Vector2)data;
                        if (oldData != null && v == (Vector2)oldData) return;
                        m.Append(v.x);
                        m.Append(v.y);
                    }
                    break;

                case "Vector3":
                    {
                        Vector3 v = (Vector3)data;
                        if (oldData != null && v == (Vector3)oldData) return;
                        m.Append(v.x);
                        m.Append(v.y);
                        m.Append(v.z);
                    }
                    break;

                case "Color":
                case "Vector4":
                    {
                        Color color = dataType == "Color" ? (Color)data : (Color)(Vector4)data;
                        Color oldColor = dataType == "Color" ? (Color)oldData : (Color)(Vector4)oldData;
                        if (oldData != null && color == (Color)oldColor) return;
                        m.Append(color.r);
                        m.Append(color.g);
                        m.Append(color.b);
                        m.Append(color.a);
                    }
                    break;

                case "Quaternion":
                    {
                        Vector3 v = ((Quaternion)data).eulerAngles;
                        if (oldData != null && v == ((Quaternion)oldData).eulerAngles) return;

                        m.Append(v.x);
                        m.Append(v.y);
                        m.Append(v.z);
                    }
                    break;

                default:
                    if (oldData != null && data.ToString() == oldData.ToString()) return;
                    if (info.type.IsEnum) m.Append(data.ToString());
                    else m.Append(data);
                    break;
            }

            query.sendData(m.BinaryData);
        }


        //VFX Helpers

        object getVFXPropValue(VisualEffect vfx, Type type, string id)
        {
            switch (type.ToString())
            {
                case "System.String":
                case "System.Char":
                    break;

                case "System.Boolean":
                    return vfx.GetBool(id);

                case "System.Int32":
                case "System.Int64":
                case "System.UInt32":
                case "System.Int16":
                case "System.UInt16":
                case "System.Byte":
                case "System.SByte":
                    return vfx.GetInt(id);

                case "System.Double":
                case "System.Single":
                    return vfx.GetFloat(id);

                case "UnityEngine.Vector2":
                    return vfx.GetVector2(id);

                case "UnityEngine.Vector3":
                    return vfx.GetVector3(id);

                case "UnityEngine.Quaternion":
                    break;

                case "UnityEngine.Color":
                case "UnityEngine.Vector4":
                    return vfx.GetVector4(id);
            }

            Debug.LogWarning("VFX prop type not handled : " + type.ToString());
            return null;
        }

        void setVFXPropValue(VisualEffect vfx, Type type, string id, object value)
        {
            switch (type.ToString())
            {
                case "System.String":
                case "System.Char":
                    break;

                case "System.Boolean":
                    vfx.SetBool(id, (bool)value);
                    break;

                case "System.Int32":
                case "System.Int64":
                case "System.UInt32":
                case "System.Int16":
                case "System.UInt16":
                case "System.Byte":
                case "System.SByte":
                    vfx.SetInt(id, (int)value);
                    break;

                case "System.Double":
                case "System.Single":
                    vfx.SetFloat(id, (float)value);
                    break;

                case "UnityEngine.Vector2":
                    vfx.SetVector2(id, (Vector2)value);
                    break;

                case "UnityEngine.Vector3":
                    vfx.SetVector3(id, (Vector3)value);
                    break;

                case "UnityEngine.Quaternion":
                    break;

                case "UnityEngine.Color":
                case "UnityEngine.Vector4":
                    vfx.SetVector4(id, (Color)value);
                    break;
            }

        }

        // Helper
        float getFloatArg(object data)
        {
            return (data is int)? (float)(int)data : (float)data;
        }
    }
}