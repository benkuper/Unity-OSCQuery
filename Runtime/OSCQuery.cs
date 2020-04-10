using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using UnityEngine;
using OSCQuery.UnityOSC;

namespace OSCQuery
{
    public class CompInfo
    {
        public CompInfo(Component comp, FieldInfo info)
        {
            isField = true;
            fieldInfo = info;
            type = fieldInfo.FieldType;
            this.comp = comp;
        }

        public CompInfo(Component comp, PropertyInfo info)
        {
            isField = false;
            propInfo = info;
            type = propInfo.PropertyType;
            this.comp = comp;
        }

        public Component comp;
        public bool isField;
        public Type type;
        public FieldInfo fieldInfo;
        public PropertyInfo propInfo;
    }

    public class OSCQuery : MonoBehaviour
    {
        [Header("Network settings")]
        public int localPort = 9010;

        [Header("Setup & Filters")]
        public GameObject rootObject;
        public enum ObjectFilterMode { All, Include, Exclude }
        public ObjectFilterMode objectFilterMode;
        public List<GameObject> filteredObjects;


        HttpListener listener;
        Thread serverThread;

        OSCReceiver receiver;

        Dictionary<string, CompInfo> compInfoMap;

        JSONObject queryData;

        void Awake()
        {
            if (!HttpListener.IsSupported)
            {
                Debug.LogError("Http server not supported !");
                return;
            }

            listener = new HttpListener();
            listener.Prefixes.Add("http://*:" + localPort + "/");

            rebuildDataTree();
        }

        private void OnEnable()
        {
            if (listener != null) listener.Start();

            serverThread = new Thread(RunThread);
            serverThread.Start();

            if (Application.isPlaying)
            {
                receiver = new OSCReceiver();
                receiver.Open(localPort);
            }
        }

        private void OnDisable()
        {
            if (serverThread != null) serverThread.Abort();
            if (listener != null) listener.Stop();
            if (receiver != null) receiver.Close();
        }

        // Update is called once per frame
        void Update()
        {
            ProcessIncomingMessages();
        }

        void RunThread()
        {
            while (true)
            {
                HttpListenerContext context = listener.GetContext();


                HttpListenerResponse response = context.Response;
                response.AddHeader("Content-Type", "application/json");
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes(queryData.ToString(true));

                // Get a response stream and write the response to it.
                response.ContentLength64 = buffer.Length;
                System.IO.Stream output = response.OutputStream;
                output.Write(buffer, 0, buffer.Length);
                output.Close();
            }
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
                if (!checkFilteredComp(comp.GetType())) continue;

                int dotIndex = comp.GetType().ToString().LastIndexOf(".");
                string compType = comp.GetType().ToString().Substring(Mathf.Max(dotIndex + 1, 0));

                string compAddress = baseAddress + "/" + compType;

                JSONObject cco = new JSONObject();
                cco.SetField("ACCESS", 0);

                JSONObject ccco = new JSONObject();

                FieldInfo[] fields = comp.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public);

                foreach (FieldInfo info in fields)
                {
                    RangeAttribute rangeAttribute = info.GetCustomAttribute<RangeAttribute>();
                    JSONObject io = getPropObject(info.FieldType, info.GetValue(comp), rangeAttribute);

                    if (io != null)
                    {
                        string ioName = SanitizeName(info.Name);
                        string fullPath = compAddress + "/" + ioName;
                        io.SetField("FULL_PATH", fullPath);
                        ccco.SetField(ioName, io);
                        compInfoMap.Add(fullPath, new CompInfo(comp, info));
                    }
                }

                PropertyInfo[] props = comp.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.SetProperty) ;
                foreach (PropertyInfo info in props)
                {
                    if (!info.CanWrite) continue;
                    string propType = info.PropertyType.ToString();
                    if (propType == "UnityEngine.Component") continue; //fix deprecation error
                    if (propType == "UnityEngine.GameObject") continue; //fix deprecation error
                    if (propType == "UnityEngine.Matrix4x4") continue; //fix deprecation error
                    if (propType == "UnityEngine.Transform") continue; //fix deprecation error
                    if (info.Name == "name" || info.Name == "tag") continue;

                    RangeAttribute rangeAttribute = info.GetCustomAttribute<RangeAttribute>();
                    JSONObject io = getPropObject(info.PropertyType, info.GetValue(comp), rangeAttribute);

                    if (io != null)
                    {
                        string ioName = SanitizeName(info.Name);
                        string fullPath = compAddress + "/" + ioName;
                        io.SetField("FULL_PATH", fullPath);
                        ccco.SetField(SanitizeName(info.Name), io);
                        compInfoMap.Add(fullPath, new CompInfo(comp, info));
                    }
                }

                cco.SetField("CONTENTS", ccco);
                co.SetField(SanitizeName(compType), cco);
            }

            o.SetField("CONTENTS", co);

            return o;
        }

        JSONObject getPropObject(Type type, object value, RangeAttribute range)
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
                    poType = "b";
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
            return objectFilterMode == ObjectFilterMode.All
                || (objectFilterMode == ObjectFilterMode.Include && filteredObjects.Contains(go))
                || (objectFilterMode == ObjectFilterMode.Exclude && !filteredObjects.Contains(go));
        }

        bool checkFilteredComp(Type type)
        {
            
            return true;
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
                            data = new Vector3((float)msg.Data[0], (float)msg.Data[1],(float)msg.Data[2]);

                            break;

                        case "UnityEngine.Quaternion":
                            data = Quaternion.Euler(new Vector3((float)msg.Data[0], (float)msg.Data[1], (float)msg.Data[2]));
                            break;

                        case "UnityEngine.Color":
                            {
                                data = (Color)msg.Data[0];
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

                    if(data != null)
                    {
                        if (info.isField) info.fieldInfo.SetValue(info.comp, data);
                        else info.propInfo.SetValue(info.comp, data);
                    }
                    else
                    {
                        Debug.LogWarning("Type not handled : " + typeString+", address : "+msg.Address);
                    }
                }
                else
                {
                    Debug.LogWarning("Property not found for address : " + msg.Address);
                }
            }
        }
    }
}