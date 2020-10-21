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


        public Component comp;

        public enum InfoType { Property, Field, Method };
        public InfoType infoType;

        public Type type;
        public FieldInfo fieldInfo;
        public PropertyInfo propInfo;
        public MethodInfo methodInfo;
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

        Dictionary<string, WSQuery> propQueryMap;
        bool propQueryMapLock;

        RegisterService zeroconfService;
        RegisterService oscService;

        void Awake()
        {
        }

        private void OnEnable()
        {
            if (propQueryMap == null) propQueryMap = new Dictionary<string, WSQuery>();

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
            foreach (KeyValuePair<string, WSQuery> aqm in propQueryMap)
            {
                sendFeedback(aqm.Key, aqm.Value);
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
            return q;
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
                    propQueryMap.Add(data, query);
                }
                else if (command == "IGNORE")
                {
                    propQueryMap.Remove(data);
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

                if (!checkFilteredComp(compType)) continue;


                string compAddress = baseAddress + "/" + compType;

                JSONObject cco = new JSONObject();
                cco.SetField("ACCESS", 0);

                JSONObject ccco = new JSONObject();

                FieldInfo[] fields = comp.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public);

                foreach (FieldInfo info in fields)
                {
                    RangeAttribute rangeAttribute = info.GetCustomAttribute<RangeAttribute>();

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

                if (compType != "Transform") //Avoid methods of internal components
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
                ro0.Add(range.min);
                ro0.Add(range.max);
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
            while (receiver.hasWaitingMessages())
            {
                OSCMessage msg = receiver.getNextMessage();

                string args = "";
                msg.Data.ForEach((arg) => args += arg.ToString() + ", ");

                if (compInfoMap.ContainsKey(msg.Address))
                {
                    CompInfo info = compInfoMap[msg.Address];
                    object data = null;

                    if (info == null)
                    {
                        Debug.LogWarning("Address not found : " + msg.Address);
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
                            data = (float)msg.Data[0];
                            break;

                        case "UnityEngine.Vector2":
                            data = new Vector2((float)msg.Data[0], (float)msg.Data[1]);
                            break;

                        case "UnityEngine.Vector3":
                            data = new Vector3((float)msg.Data[0], (float)msg.Data[1], (float)msg.Data[2]);

                            break;

                        case "UnityEngine.Quaternion":
                            data = Quaternion.Euler(new Vector3((float)msg.Data[0], (float)msg.Data[1], (float)msg.Data[2]));
                            break;

                        case "UnityEngine.Color":
                            {
                                if (msg.Data.Count == 1) data = (Color)msg.Data[0];
                                else if (msg.Data.Count >= 3) data = new Color((float)msg.Data[0], (float)msg.Data[1], (float)msg.Data[2], msg.Data.Count > 3 ? (float)msg.Data[2] : 1.0f);
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
                                        Debug.Log("Found enum " + field.Name + " > " + field.GetRawConstantValue());
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
            CompInfo info = compInfoMap[address];

            object data = null;
            if (info.propInfo != null) data = info.propInfo.GetValue(info.comp);
            else if (info.fieldInfo != null) data = info.fieldInfo.GetValue(info.comp);

            if (data != null)
            {
                OSCMessage m = new OSCMessage(address);

                string dataType = data.GetType().Name;
                switch (dataType)
                {
                    case "Boolean":
                        {
                            bool val = (bool)data;
                            m.Append(val ? 1 : 0);
                        }
                        break;

                    case "Vector2":
                        {
                            Vector2 v = (Vector2)data;
                            m.Append(v.x);
                            m.Append(v.y);
                        }
                        break;

                    case "Vector3":
                        {
                            Vector3 v = (Vector3)data;
                            m.Append(v.x);
                            m.Append(v.y);
                            m.Append(v.z);
                        }
                        break;

                    case "Color":
                        {
                            Color color = (Color)data;
                            m.Append(color.r);
                            m.Append(color.g);
                            m.Append(color.b);
                            m.Append(color.a);
                        }
                        break;

                    case "Quaternion":
                        {
                            Vector3 v = ((Quaternion)data).eulerAngles;
                            m.Append(v.x);
                            m.Append(v.y);
                            m.Append(v.z);
                        }
                        break;

                    default:
                        if(info.type.IsEnum) m.Append(data.ToString());
                        else m.Append(data);
                        break;
                }

                Debug.Log("Send data here ! "+ m.BinaryData.Length+" bytes");
                query.sendData(m.BinaryData);
            }
        }
    }
}