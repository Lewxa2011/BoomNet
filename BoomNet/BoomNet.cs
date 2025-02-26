using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace BoomNet
{
    // network synchronization interface (this is the PUN IPunObservable equivalent)
    public interface INetworkObservable
    {
        void OnNetworkSerialize(NetworkStream stream, bool isWriting);
    }

    // network stream for serialization
    public class NetworkStream
    {
        private List<byte> data = new List<byte>();
        private int readPosition = 0;

        public void WriteFloat(float value) => data.AddRange(BitConverter.GetBytes(value));
        public void WriteVector3(Vector3 value)
        {
            WriteFloat(value.x);
            WriteFloat(value.y);
            WriteFloat(value.z);
        }
        public void WriteQuaternion(Quaternion value)
        {
            WriteFloat(value.x);
            WriteFloat(value.y);
            WriteFloat(value.z);
            WriteFloat(value.w);
        }
        public void WriteBool(bool value) => data.Add((byte)(value ? 1 : 0));
        public void WriteInt(int value) => data.AddRange(BitConverter.GetBytes(value));
        public void WriteString(string value)
        {
            byte[] strBytes = System.Text.Encoding.UTF8.GetBytes(value);
            WriteInt(strBytes.Length);
            data.AddRange(strBytes);
        }

        public float ReadFloat()
        {
            float value = BitConverter.ToSingle(data.ToArray(), readPosition);
            readPosition += 4;
            return value;
        }
        public Vector3 ReadVector3() => new Vector3(ReadFloat(), ReadFloat(), ReadFloat());
        public Quaternion ReadQuaternion() => new Quaternion(ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat());
        public bool ReadBool() => data[readPosition++] != 0;
        public int ReadInt()
        {
            int value = BitConverter.ToInt32(data.ToArray(), readPosition);
            readPosition += 4;
            return value;
        }
        public string ReadString()
        {
            int length = ReadInt();
            string value = System.Text.Encoding.UTF8.GetString(data.ToArray(), readPosition, length);
            readPosition += length;
            return value;
        }

        public byte[] ToArray() => data.ToArray();
        public void FromArray(byte[] array)
        {
            data.Clear();
            data.AddRange(array);
            readPosition = 0;
        }
    }

    // this is the main networking manager class
    public class NetworkManager : MonoBehaviour
    {
        public static NetworkManager Instance { get; private set; }
        public int Port => port;
        [SerializeField] private int port = 7777;
        [SerializeField] private int maxPlayers = 16;
        [SerializeField] private float clientTimeoutSeconds = 10f;
        [SerializeField] private float tickRate = 20f;

        public bool IsServer { get; private set; }
        public bool IsClient { get; private set; }
        public bool IsConnected => IsServer || (IsClient && clientSocket != null);
        public int LocalPlayerId { get; private set; } = -1;

        private Socket serverSocket;
        private Socket clientSocket;
        private List<ClientInfo> connectedClients = new List<ClientInfo>();
        private Dictionary<int, NetworkView> networkViews = new Dictionary<int, NetworkView>();
        private Queue<NetworkPacket> incomingPackets = new Queue<NetworkPacket>();
        private float lastTickTime;
        private int nextNetworkViewId = 1;
        private EndPoint remoteEndPoint;

        public event Action<int> OnClientConnected;
        public event Action<int> OnClientDisconnected;
        public event Action OnConnectedToServer;
        public event Action OnDisconnectedFromServer;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            if (!IsConnected) return;

            ProcessIncomingPackets();

            if (Time.time - lastTickTime >= 1f / tickRate)
            {
                if (IsServer) ServerTick();
                else if (IsClient) ClientTick();

                lastTickTime = Time.time;
            }
        }

        public bool StartServer()
        {
            try
            {
                serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                serverSocket.Bind(new IPEndPoint(IPAddress.Any, port));

                Debug.Log($"Server started on port {port}");
                IsServer = true;

                Thread receiveThread = new Thread(ServerReceiveLoop);
                receiveThread.IsBackground = true;
                receiveThread.Start();

                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to start server: {e.Message}");
                CloseServer();
                return false;
            }
        }

        public void CloseServer()
        {
            if (!IsServer) return;

            foreach (var client in connectedClients)
            {
                SendPacket(CreatePacket(PacketType.Disconnect, "Server shutting down"), client.EndPoint);
            }

            serverSocket?.Close();
            serverSocket = null;
            IsServer = false;
            connectedClients.Clear();
            Debug.Log("Server closed");
        }

        private void ServerReceiveLoop()
        {
            byte[] buffer = new byte[4096];
            EndPoint clientEndPoint = new IPEndPoint(IPAddress.Any, 0);

            while (IsServer && serverSocket != null)
            {
                try
                {
                    int size = serverSocket.ReceiveFrom(buffer, ref clientEndPoint);
                    byte[] data = new byte[size];
                    Array.Copy(buffer, data, size);

                    NetworkPacket packet = NetworkPacket.Deserialize(data);
                    packet.SenderEndPoint = clientEndPoint;

                    lock (incomingPackets)
                    {
                        incomingPackets.Enqueue(packet);
                    }
                }
                catch (Exception e)
                {
                    if (IsServer)
                    {
                        Debug.LogError($"Server receive error: {e.Message}");
                    }
                }
            }
        }

        private void ServerTick()
        {
            CheckClientTimeouts();
            SynchronizeViews();
        }

        private void CheckClientTimeouts()
        {
            float currentTime = Time.time;
            List<ClientInfo> timedOutClients = new List<ClientInfo>();

            foreach (var client in connectedClients)
            {
                if (currentTime - client.LastMessageTime > clientTimeoutSeconds)
                {
                    timedOutClients.Add(client);
                }
            }

            foreach (var client in timedOutClients)
            {
                DisconnectClient(client.ClientId, "Connection timed out");
            }
        }

        private void SynchronizeViews()
        {
            Dictionary<int, ViewState> viewStates = new Dictionary<int, ViewState>();

            foreach (var netView in networkViews.Values)
            {
                if (netView != null && netView.gameObject.activeInHierarchy && netView.Observed != null)
                {
                    viewStates[netView.ViewId] = netView.CaptureState();
                }
            }

            ViewSyncState syncState = new ViewSyncState
            {
                Timestamp = Time.time,
                ViewStates = viewStates
            };

            byte[] stateData = syncState.Serialize();
            NetworkPacket statePacket = CreatePacket(PacketType.ViewSyncState, stateData);

            foreach (var client in connectedClients)
            {
                SendPacket(statePacket, client.EndPoint);
            }
        }

        private int HandleConnectionRequest(EndPoint clientEndPoint, string clientData)
        {
            foreach (var client in connectedClients)
            {
                if (client.EndPoint.Equals(clientEndPoint))
                {
                    return client.ClientId;
                }
            }

            if (connectedClients.Count >= maxPlayers)
            {
                SendPacket(CreatePacket(PacketType.ConnectionRejected, "Server is full"), clientEndPoint);
                return -1;
            }

            int clientId = GenerateClientId();

            ClientInfo newClient = new ClientInfo
            {
                ClientId = clientId,
                EndPoint = clientEndPoint,
                LastMessageTime = Time.time,
                ClientData = clientData
            };

            connectedClients.Add(newClient);

            ConnectionAccepted acceptData = new ConnectionAccepted
            {
                ClientId = clientId,
                ServerTime = Time.time
            };

            SendPacket(CreatePacket(PacketType.ConnectionAccepted, acceptData.Serialize()), clientEndPoint);

            BroadcastPacket(CreatePacket(PacketType.ClientConnected, clientId.ToString()), clientId);

            OnClientConnected?.Invoke(clientId);

            Debug.Log($"Client {clientId} connected from {clientEndPoint}");
            return clientId;
        }

        private void DisconnectClient(int clientId, string reason)
        {
            ClientInfo client = connectedClients.Find(c => c.ClientId == clientId);
            if (client == null) return;

            SendPacket(CreatePacket(PacketType.Disconnect, reason), client.EndPoint);

            connectedClients.Remove(client);

            BroadcastPacket(CreatePacket(PacketType.ClientDisconnected, clientId.ToString()));

            OnClientDisconnected?.Invoke(clientId);

            Debug.Log($"Client {clientId} disconnected: {reason}");
        }

        private int GenerateClientId()
        {
            int id = UnityEngine.Random.Range(1, 10000);
            while (connectedClients.Exists(c => c.ClientId == id))
            {
                id = UnityEngine.Random.Range(1, 10000);
            }
            return id;
        }

        public bool ConnectToServer(string serverIP, int serverPort, string clientData = "")
        {
            try
            {
                clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                IPEndPoint serverEndPoint = new IPEndPoint(IPAddress.Parse(serverIP), serverPort);

                remoteEndPoint = serverEndPoint;

                IsClient = true;

                Thread receiveThread = new Thread(ClientReceiveLoop);
                receiveThread.IsBackground = true;
                receiveThread.Start();

                SendPacket(CreatePacket(PacketType.ConnectionRequest, clientData));

                Debug.Log($"Connecting to server at {serverIP}:{serverPort}...");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to connect to server: {e.Message}");
                DisconnectFromServer();
                return false;
            }
        }

        public void DisconnectFromServer()
        {
            if (!IsClient) return;

            if (LocalPlayerId != -1)
            {
                SendPacket(CreatePacket(PacketType.Disconnect, "Client disconnecting"));
            }

            clientSocket?.Close();
            clientSocket = null;
            IsClient = false;
            LocalPlayerId = -1;

            OnDisconnectedFromServer?.Invoke();
            Debug.Log("Disconnected from server");
        }

        private void ClientReceiveLoop()
        {
            byte[] buffer = new byte[4096];
            EndPoint serverEP = remoteEndPoint;

            while (IsClient && clientSocket != null)
            {
                try
                {
                    int size = clientSocket.ReceiveFrom(buffer, ref serverEP);
                    byte[] data = new byte[size];
                    Array.Copy(buffer, data, size);

                    NetworkPacket packet = NetworkPacket.Deserialize(data);
                    packet.SenderEndPoint = serverEP;

                    lock (incomingPackets)
                    {
                        incomingPackets.Enqueue(packet);
                    }
                }
                catch (Exception e)
                {
                    if (IsClient)
                    {
                        Debug.LogError($"Client receive error: {e.Message}");
                    }
                }
            }
        }

        private void ClientTick()
        {
            SendInputState();
        }

        private void SendInputState()
        {
            if (LocalPlayerId == -1) return;

            Dictionary<int, InputState> inputStates = new Dictionary<int, InputState>();

            foreach (var netView in networkViews.Values)
            {
                if (netView != null && netView.IsMine && netView.gameObject.activeInHierarchy)
                {
                    InputState inputState = netView.CaptureInput();
                    if (inputState != null)
                    {
                        inputStates[netView.ViewId] = inputState;
                    }
                }
            }

            ClientInput inputPacket = new ClientInput
            {
                ClientId = LocalPlayerId,
                Timestamp = Time.time,
                Inputs = inputStates
            };

            SendPacket(CreatePacket(PacketType.ClientInput, inputPacket.Serialize()));
        }

        private void ProcessIncomingPackets()
        {
            NetworkPacket packet;
            bool hasPackets = false;

            lock (incomingPackets)
            {
                hasPackets = incomingPackets.Count > 0;
            }

            while (hasPackets)
            {
                lock (incomingPackets)
                {
                    packet = incomingPackets.Dequeue();
                    hasPackets = incomingPackets.Count > 0;
                }

                HandlePacket(packet);
            }
        }

        private void HandlePacket(NetworkPacket packet)
        {
            switch (packet.Type)
            {
                case PacketType.ConnectionRequest:
                    if (IsServer)
                    {
                        HandleConnectionRequest(packet.SenderEndPoint, packet.GetString());
                    }
                    break;

                case PacketType.ConnectionAccepted:
                    if (IsClient)
                    {
                        ConnectionAccepted acceptData = ConnectionAccepted.Deserialize(packet.GetBytes());
                        LocalPlayerId = acceptData.ClientId;
                        Debug.Log($"Connected to server with client ID: {LocalPlayerId}");
                        OnConnectedToServer?.Invoke();
                    }
                    break;

                case PacketType.ConnectionRejected:
                    if (IsClient)
                    {
                        Debug.Log($"Connection rejected: {packet.GetString()}");
                        DisconnectFromServer();
                    }
                    break;

                case PacketType.Disconnect:
                    if (IsServer)
                    {
                        ClientInfo client = connectedClients.Find(c => c.EndPoint.Equals(packet.SenderEndPoint));
                        if (client != null)
                        {
                            DisconnectClient(client.ClientId, packet.GetString());
                        }
                    }
                    else if (IsClient)
                    {
                        Debug.Log($"Disconnected from server: {packet.GetString()}");
                        DisconnectFromServer();
                    }
                    break;

                case PacketType.ClientConnected:
                    int newClientId = int.Parse(packet.GetString());
                    Debug.Log($"Client {newClientId} connected");
                    break;

                case PacketType.ClientDisconnected:
                    int disconnectedClientId = int.Parse(packet.GetString());
                    Debug.Log($"Client {disconnectedClientId} disconnected");
                    break;

                case PacketType.ViewSyncState:
                    if (IsClient)
                    {
                        ViewSyncState syncState = ViewSyncState.Deserialize(packet.GetBytes());
                        UpdateViewStates(syncState);
                    }
                    break;

                case PacketType.ClientInput:
                    if (IsServer)
                    {
                        ClientInput inputData = ClientInput.Deserialize(packet.GetBytes());
                        ProcessClientInput(inputData);

                        ClientInfo clientInfo = connectedClients.Find(c => c.ClientId == inputData.ClientId);
                        if (clientInfo != null)
                        {
                            clientInfo.LastMessageTime = Time.time;
                        }
                    }
                    break;

                case PacketType.SpawnView:
                    if (IsClient)
                    {
                        SpawnViewData spawnData = SpawnViewData.Deserialize(packet.GetBytes());
                        SpawnNetworkView(spawnData);
                    }
                    break;

                case PacketType.DestroyView:
                    int viewId = int.Parse(packet.GetString());
                    DestroyNetworkView(viewId);
                    break;

                case PacketType.RPC:
                    RPCData rpcData = RPCData.Deserialize(packet.GetBytes());
                    HandleRPC(rpcData);
                    break;
            }
        }

        private void SendPacket(NetworkPacket packet, EndPoint endpoint = null)
        {
            if (endpoint == null)
            {
                if (IsClient)
                {
                    endpoint = remoteEndPoint;
                }
                else
                {
                    Debug.LogError("Attempted to send packet without endpoint");
                    return;
                }
            }

            try
            {
                byte[] data = packet.Serialize();

                if (IsServer)
                {
                    serverSocket?.SendTo(data, endpoint);
                }
                else if (IsClient)
                {
                    clientSocket?.SendTo(data, endpoint);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to send packet: {e.Message}");
            }
        }

        private void BroadcastPacket(NetworkPacket packet, int excludeClientId = -1)
        {
            if (!IsServer) return;

            foreach (var client in connectedClients)
            {
                if (client.ClientId != excludeClientId)
                {
                    SendPacket(packet, client.EndPoint);
                }
            }
        }

        private NetworkPacket CreatePacket(PacketType type, string message)
        {
            return new NetworkPacket
            {
                Type = type,
                Data = System.Text.Encoding.UTF8.GetBytes(message)
            };
        }

        private NetworkPacket CreatePacket(PacketType type, byte[] data)
        {
            return new NetworkPacket
            {
                Type = type,
                Data = data
            };
        }

        public int GetNextViewId()
        {
            if (!IsServer) return -1;
            return nextNetworkViewId++;
        }

        public void RegisterNetworkView(NetworkView netView)
        {
            if (netView == null || networkViews.ContainsKey(netView.ViewId)) return;

            networkViews[netView.ViewId] = netView;

            if (IsServer && netView.ViewId > 0)
            {
                SpawnNetworkView(netView);
            }
        }

        public void UnregisterNetworkView(NetworkView netView)
        {
            if (netView == null || !networkViews.ContainsKey(netView.ViewId)) return;

            networkViews.Remove(netView.ViewId);

            if (IsServer)
            {
                BroadcastPacket(CreatePacket(PacketType.DestroyView, netView.ViewId.ToString()));
            }
        }

        private void SpawnNetworkView(NetworkView netView)
        {
            if (!IsServer) return;

            SpawnViewData spawnData = new SpawnViewData
            {
                ViewId = netView.ViewId,
                PrefabId = netView.PrefabId,
                OwnerId = netView.OwnerId,
                InitialState = netView.CaptureState()
            };

            BroadcastPacket(CreatePacket(PacketType.SpawnView, spawnData.Serialize()));
        }

        private void SpawnNetworkView(SpawnViewData spawnData)
        {
            if (networkViews.ContainsKey(spawnData.ViewId)) return;

            GameObject prefab = NetworkPrefabRegistry.GetPrefab(spawnData.PrefabId);
            if (prefab == null)
            {
                Debug.LogError($"Failed to find network prefab with ID: {spawnData.PrefabId}");
                return;
            }

            GameObject instance = Instantiate(prefab);
            NetworkView netView = instance.GetComponent<NetworkView>();

            if (netView == null)
            {
                Debug.LogError("Spawned prefab doesn't have NetworkView component");
                Destroy(instance);
                return;
            }

            netView.InitializeFromSpawn(spawnData);

            networkViews[spawnData.ViewId] = netView;

            netView.ApplyState(spawnData.InitialState);
        }

        private void DestroyNetworkView(int viewId)
        {
            if (!networkViews.TryGetValue(viewId, out NetworkView netView)) return;

            networkViews.Remove(viewId);

            if (netView != null && netView.gameObject != null)
            {
                Destroy(netView.gameObject);
            }
        }

        private void UpdateViewStates(ViewSyncState syncState)
        {
            foreach (var kvp in syncState.ViewStates)
            {
                int viewId = kvp.Key;
                ViewState state = kvp.Value;

                if (networkViews.TryGetValue(viewId, out NetworkView netView))
                {
                    netView.ApplyState(state);
                }
            }
        }

        private void ProcessClientInput(ClientInput inputData)
        {
            foreach (var kvp in inputData.Inputs)
            {
                int viewId = kvp.Key;
                InputState inputState = kvp.Value;

                if (networkViews.TryGetValue(viewId, out NetworkView netView))
                {
                    if (netView.OwnerId == inputData.ClientId)
                    {
                        netView.ApplyInput(inputState);
                    }
                }
            }
        }

        public void SendRPC(int viewId, string methodName, object[] parameters, int targetClientId = -1)
        {
            if (!IsServer && !IsClient) return;

            RPCData rpcData = new RPCData
            {
                ViewId = viewId,
                MethodName = methodName,
                Parameters = RPCSerializer.SerializeParameters(parameters)
            };

            NetworkPacket packet = CreatePacket(PacketType.RPC, rpcData.Serialize());

            if (IsServer)
            {
                if (targetClientId == -1)
                {
                    BroadcastPacket(packet);
                }
                else
                {
                    ClientInfo client = connectedClients.Find(c => c.ClientId == targetClientId);
                    if (client != null)
                    {
                        SendPacket(packet, client.EndPoint);
                    }
                }
            }
            else if (IsClient)
            {
                SendPacket(packet);
            }
        }

        private void HandleRPC(RPCData rpcData)
        {
            if (!networkViews.TryGetValue(rpcData.ViewId, out NetworkView netView)) return;

            netView.InvokeRPC(rpcData.MethodName, RPCSerializer.DeserializeParameters(rpcData.Parameters));
        }

        private void OnApplicationQuit()
        {
            if (IsServer) CloseServer();
            if (IsClient) DisconnectFromServer();
        }
    }

    // PUN PhotonView equivalent
    [AddComponentMenu("Networking/Network View")]
    public class NetworkView : MonoBehaviour
    {
        [SerializeField] private string prefabIdString;
        [SerializeField] private Component observed;

        public int ViewId { get; private set; } = -1;
        public int PrefabId => prefabIdString.GetHashCode();
        public int OwnerId { get; private set; } = -1;

        public bool IsMine => OwnerId == NetworkManager.Instance.LocalPlayerId;

        // component being observed (for transform/object sync)
        public Component Observed
        {
            get => observed;
            set => observed = value;
        }

        public void Initialize(int viewId, int ownerId)
        {
            ViewId = viewId;
            OwnerId = ownerId;

            NetworkManager.Instance.RegisterNetworkView(this);
        }

        public void InitializeFromSpawn(SpawnViewData spawnData)
        {
            ViewId = spawnData.ViewId;
            OwnerId = spawnData.OwnerId;
        }

        public ViewState CaptureState()
        {
            ViewState state = new ViewState
            {
                Position = transform.position,
                Rotation = transform.rotation,
                Scale = transform.localScale
            };

            if (observed != null)
            {
                NetworkStream stream = new NetworkStream();

                if (observed is INetworkObservable observable)
                {
                    observable.OnNetworkSerialize(stream, true);
                    state.ObservedData = stream.ToArray();
                }
                else if (observed is NetworkTransformView)
                {
                    ((NetworkTransformView)observed).OnNetworkSerialize(stream, true);
                    state.ObservedData = stream.ToArray();
                }
            }

            return state;
        }

        public void ApplyState(ViewState state)
        {
            if (!IsMine)
            {
                transform.position = state.Position;
                transform.rotation = state.Rotation;
                transform.localScale = state.Scale;

                if (observed != null && state.ObservedData != null)
                {
                    NetworkStream stream = new NetworkStream();
                    stream.FromArray(state.ObservedData);

                    if (observed is INetworkObservable observable)
                    {
                        observable.OnNetworkSerialize(stream, false);
                    }
                    else if (observed is NetworkTransformView)
                    {
                        ((NetworkTransformView)observed).OnNetworkSerialize(stream, false);
                    }
                }
            }
        }

        public InputState CaptureInput()
        {
            if (!IsMine) return null;

            // only need to capture input for controllersz
            if (observed is NetworkController controller)
            {
                return controller.CaptureInputState();
            }

            return null;
        }

        public void ApplyInput(InputState inputState)
        {
            if (inputState == null) return;

            if (observed is NetworkController controller)
            {
                controller.ApplyInputState(inputState);
            }
        }

        public void InvokeRPC(string methodName, object[] parameters)
        {
            System.Reflection.MethodInfo method = GetType().GetMethod(
                methodName,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic
            );

            if (method != null)
            {
                method.Invoke(this, parameters);
            }
            else
            {
                MonoBehaviour[] behaviors = GetComponents<MonoBehaviour>();

                foreach (var behavior in behaviors)
                {
                    method = behavior.GetType().GetMethod(
                        methodName,
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic
                    );

                    if (method != null)
                    {
                        method.Invoke(behavior, parameters);
                        break;
                    }
                }
            }
        }

        public void RPC(string methodName, RPCTarget target, params object[] parameters)
        {
            if (NetworkManager.Instance == null) return;

            int targetClientId = -1;

            switch (target)
            {
                case RPCTarget.All:
                    targetClientId = -1;
                    break;
                case RPCTarget.Others:
                    if (NetworkManager.Instance.IsServer)
                    {
                        // send to all except owner
                        targetClientId = -2; // special value handled by NetworkManager
                    }
                    break;
                case RPCTarget.Owner:
                    targetClientId = OwnerId;
                    break;
                case RPCTarget.Server:
                    if (NetworkManager.Instance.IsClient && !NetworkManager.Instance.IsServer)
                    {
                        NetworkManager.Instance.SendRPC(ViewId, methodName, parameters);
                    }
                    return;
            }

            if (NetworkManager.Instance.IsServer)
            {
                NetworkManager.Instance.SendRPC(ViewId, methodName, parameters, targetClientId);

                // also execute locally if All was specified
                if (target == RPCTarget.All)
                {
                    InvokeRPC(methodName, parameters);
                }
            }
            else if (NetworkManager.Instance.IsClient)
            {
                // send to server to relay
                NetworkManager.Instance.SendRPC(ViewId, methodName, parameters);
            }
        }

        private void OnDestroy()
        {
            if (NetworkManager.Instance != null)
            {
                NetworkManager.Instance.UnregisterNetworkView(this);
            }
        }
    }

    // PUN PhotonTransformView equivalent
    [AddComponentMenu("Networking/Network Transform View")]
    public class NetworkTransformView : MonoBehaviour
    {
        [Tooltip("Position sync mode")]
        public SyncMode syncPosition = SyncMode.Sync;

        [Tooltip("Rotation sync mode")]
        public SyncMode syncRotation = SyncMode.Sync;

        [Tooltip("Scale sync mode")]
        public SyncMode syncScale = SyncMode.Sync;

        [Tooltip("Interpolation speed for position")]
        public float positionLerpSpeed = 10f;

        [Tooltip("Interpolation speed for rotation")]
        public float rotationLerpSpeed = 10f;

        [Tooltip("Interpolation speed for scale")]
        public float scaleLerpSpeed = 10f;

        [Tooltip("Position threshold for synchronization")]
        public float positionThreshold = 0.01f;

        [Tooltip("Rotation threshold for synchronization in degrees")]
        public float rotationThreshold = 1.0f;

        [Tooltip("Scale threshold for synchronization")]
        public float scaleThreshold = 0.01f;

        private Vector3 networkPosition;
        private Quaternion networkRotation;
        private Vector3 networkScale;

        private Vector3 lastSentPosition;
        private Quaternion lastSentRotation;
        private Vector3 lastSentScale;

        private Transform cachedTransform;

        private void Awake()
        {
            cachedTransform = transform;

            // initialize network transforms with current values
            networkPosition = cachedTransform.position;
            networkRotation = cachedTransform.rotation;
            networkScale = cachedTransform.localScale;

            lastSentPosition = networkPosition;
            lastSentRotation = networkRotation;
            lastSentScale = networkScale;
        }

        private void Update()
        {
            NetworkView netView = GetComponent<NetworkView>();

            if (netView != null && !netView.IsMine)
            {
                // apply interpolation for non-owned objects
                if (syncPosition == SyncMode.Sync)
                {
                    cachedTransform.position = Vector3.Lerp(cachedTransform.position, networkPosition, Time.deltaTime * positionLerpSpeed);
                }

                if (syncRotation == SyncMode.Sync)
                {
                    cachedTransform.rotation = Quaternion.Slerp(cachedTransform.rotation, networkRotation, Time.deltaTime * rotationLerpSpeed);
                }

                if (syncScale == SyncMode.Sync)
                {
                    cachedTransform.localScale = Vector3.Lerp(cachedTransform.localScale, networkScale, Time.deltaTime * scaleLerpSpeed);
                }
            }
        }

        public void OnNetworkSerialize(NetworkStream stream, bool isWriting)
        {
            if (isWriting)
            {
                // only send data if it has changed significantly
                bool sendPosition = syncPosition == SyncMode.Sync &&
                                   Vector3.Distance(lastSentPosition, cachedTransform.position) > positionThreshold;

                bool sendRotation = syncRotation == SyncMode.Sync &&
                                   Quaternion.Angle(lastSentRotation, cachedTransform.rotation) > rotationThreshold;

                bool sendScale = syncScale == SyncMode.Sync &&
                                Vector3.Distance(lastSentScale, cachedTransform.localScale) > scaleThreshold;

                // write booleans for which transforms are being sent
                stream.WriteBool(sendPosition);
                stream.WriteBool(sendRotation);
                stream.WriteBool(sendScale);

                // write transform data if needed
                if (sendPosition)
                {
                    stream.WriteVector3(cachedTransform.position);
                    lastSentPosition = cachedTransform.position;
                }

                if (sendRotation)
                {
                    stream.WriteQuaternion(cachedTransform.rotation);
                    lastSentRotation = cachedTransform.rotation;
                }

                if (sendScale)
                {
                    stream.WriteVector3(cachedTransform.localScale);
                    lastSentScale = cachedTransform.localScale;
                }
            }
            else
            {
                // read which transforms are being received
                bool receivePosition = stream.ReadBool();
                bool receiveRotation = stream.ReadBool();
                bool receiveScale = stream.ReadBool();

                // read transform data if available
                if (receivePosition)
                {
                    networkPosition = stream.ReadVector3();
                }

                if (receiveRotation)
                {
                    networkRotation = stream.ReadQuaternion();
                }

                if (receiveScale)
                {
                    networkScale = stream.ReadVector3();
                }
            }
        }
    }

    // !!base class for network controllers!!
    [AddComponentMenu("Networking/Network Controller")]
    public abstract class NetworkController : MonoBehaviour
    {
        protected NetworkView networkView;

        protected virtual void Awake()
        {
            networkView = GetComponent<NetworkView>();
        }

        // !!should be implemented by derived classes to capture input!!
        public abstract InputState CaptureInputState();

        // !!should be implemented by derived classes to apply input!!
        public abstract void ApplyInputState(InputState inputState);
    }

    // prefab registry for network instantiation
    public static class NetworkPrefabRegistry
    {
        private static Dictionary<int, GameObject> prefabs = new Dictionary<int, GameObject>();

        public static void RegisterPrefab(GameObject prefab)
        {
            if (prefab != null)
            {
                NetworkView netView = prefab.GetComponent<NetworkView>();
                if (netView != null)
                {
                    int prefabId = netView.PrefabId;
                    prefabs[prefabId] = prefab;
                }
            }
        }

        public static GameObject GetPrefab(int prefabId)
        {
            if (prefabs.TryGetValue(prefabId, out GameObject prefab))
            {
                return prefab;
            }
            return null;
        }
    }

    // this is for creating network objects (this is practically PUN's PhotonNetwork.Instantiate)
    public static class NetworkInstantiator
    {
        public static GameObject Instantiate(string prefabName, Vector3 position, Quaternion rotation, int ownerId = -1)
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected)
            {
                Debug.LogError("Cannot instantiate network object: Not connected to network");
                return null;
            }

            if (ownerId == -1)
            {
                ownerId = NetworkManager.Instance.LocalPlayerId;
            }

            GameObject[] resources = Resources.LoadAll<GameObject>("");
            GameObject prefab = null;

            foreach (var obj in resources)
            {
                if (obj.name == prefabName)
                {
                    prefab = obj;
                    break;
                }
            }

            if (prefab == null)
            {
                Debug.LogError($"Cannot find network prefab: {prefabName}");
                return null;
            }

            NetworkView prefabNetView = prefab.GetComponent<NetworkView>();
            if (prefabNetView == null)
            {
                Debug.LogError($"Prefab {prefabName} does not have a NetworkView component");
                return null;
            }

            NetworkPrefabRegistry.RegisterPrefab(prefab);

            GameObject instance = GameObject.Instantiate(prefab, position, rotation);
            NetworkView netView = instance.GetComponent<NetworkView>();

            if (netView != null)
            {
                int viewId = NetworkManager.Instance.GetNextViewId();
                netView.Initialize(viewId, ownerId);
            }

            return instance;
        }
    }

    // sync modes enum
    public enum SyncMode
    {
        Disabled,
        Sync
    }

    // RPC target opts
    public enum RPCTarget
    {
        All,
        Others,
        Owner,
        Server
    }

    // this shit's helper classes for networking
    #region Network Data Classes

    public class ClientInfo
    {
        public int ClientId;
        public EndPoint EndPoint;
        public float LastMessageTime;
        public string ClientData;
    }

    public class ViewState
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;
        public byte[] ObservedData;

        public byte[] Serialize()
        {
            NetworkStream stream = new NetworkStream();
            stream.WriteVector3(Position);
            stream.WriteQuaternion(Rotation);
            stream.WriteVector3(Scale);

            stream.WriteBool(ObservedData != null);
            if (ObservedData != null)
            {
                stream.WriteInt(ObservedData.Length);
                foreach (var b in ObservedData)
                {
                    stream.WriteBool(b == 1);
                }
            }

            return stream.ToArray();
        }

        public static ViewState Deserialize(byte[] data)
        {
            NetworkStream stream = new NetworkStream();
            stream.FromArray(data);

            ViewState state = new ViewState
            {
                Position = stream.ReadVector3(),
                Rotation = stream.ReadQuaternion(),
                Scale = stream.ReadVector3()
            };

            if (stream.ReadBool())
            {
                int length = stream.ReadInt();
                state.ObservedData = new byte[length];
                for (int i = 0; i < length; i++)
                {
                    state.ObservedData[i] = (byte)(stream.ReadBool() ? 1 : 0);
                }
            }

            return state;
        }
    }

    public class ViewSyncState
    {
        public float Timestamp;
        public Dictionary<int, ViewState> ViewStates;

        public byte[] Serialize()
        {
            NetworkStream stream = new NetworkStream();
            stream.WriteFloat(Timestamp);
            stream.WriteInt(ViewStates.Count);

            foreach (var kvp in ViewStates)
            {
                stream.WriteInt(kvp.Key);
                byte[] viewStateData = kvp.Value.Serialize();
                stream.WriteInt(viewStateData.Length);
                foreach (var b in viewStateData)
                {
                    stream.WriteBool(b == 1);
                }
            }

            return stream.ToArray();
        }

        public static ViewSyncState Deserialize(byte[] data)
        {
            NetworkStream stream = new NetworkStream();
            stream.FromArray(data);

            ViewSyncState state = new ViewSyncState
            {
                Timestamp = stream.ReadFloat(),
                ViewStates = new Dictionary<int, ViewState>()
            };

            int viewCount = stream.ReadInt();
            for (int i = 0; i < viewCount; i++)
            {
                int viewId = stream.ReadInt();
                int viewStateLength = stream.ReadInt();
                byte[] viewStateData = new byte[viewStateLength];

                for (int j = 0; j < viewStateLength; j++)
                {
                    viewStateData[j] = (byte)(stream.ReadBool() ? 1 : 0);
                }

                state.ViewStates[viewId] = ViewState.Deserialize(viewStateData);
            }

            return state;
        }
    }

    public class InputState
    {
        public float Timestamp;
        public Dictionary<string, object> Values = new Dictionary<string, object>();

        public void SetValue(string key, object value)
        {
            Values[key] = value;
        }

        public T GetValue<T>(string key, T defaultValue = default)
        {
            if (Values.TryGetValue(key, out object value) && value is T typedValue)
            {
                return typedValue;
            }
            return defaultValue;
        }

        public byte[] Serialize()
        {
            NetworkStream stream = new NetworkStream();
            stream.WriteFloat(Timestamp);
            stream.WriteInt(Values.Count);

            foreach (var kvp in Values)
            {
                stream.WriteString(kvp.Key);

                if (kvp.Value is bool boolValue)
                {
                    stream.WriteInt(0); // Type code for bool
                    stream.WriteBool(boolValue);
                }
                else if (kvp.Value is int intValue)
                {
                    stream.WriteInt(1); // Type code for int
                    stream.WriteInt(intValue);
                }
                else if (kvp.Value is float floatValue)
                {
                    stream.WriteInt(2); // Type code for float
                    stream.WriteFloat(floatValue);
                }
                else if (kvp.Value is string stringValue)
                {
                    stream.WriteInt(3); // Type code for string
                    stream.WriteString(stringValue);
                }
                else if (kvp.Value is Vector3 vector3Value)
                {
                    stream.WriteInt(4); // Type code for Vector3
                    stream.WriteVector3(vector3Value);
                }
                else if (kvp.Value is Quaternion quaternionValue)
                {
                    stream.WriteInt(5); // Type code for Quaternion
                    stream.WriteQuaternion(quaternionValue);
                }
            }

            return stream.ToArray();
        }

        public static InputState Deserialize(byte[] data)
        {
            NetworkStream stream = new NetworkStream();
            stream.FromArray(data);

            InputState state = new InputState
            {
                Timestamp = stream.ReadFloat(),
                Values = new Dictionary<string, object>()
            };

            int valueCount = stream.ReadInt();
            for (int i = 0; i < valueCount; i++)
            {
                string key = stream.ReadString();
                int typeCode = stream.ReadInt();

                switch (typeCode)
                {
                    case 0: // bool
                        state.Values[key] = stream.ReadBool();
                        break;
                    case 1: // int
                        state.Values[key] = stream.ReadInt();
                        break;
                    case 2: // float
                        state.Values[key] = stream.ReadFloat();
                        break;
                    case 3: // string
                        state.Values[key] = stream.ReadString();
                        break;
                    case 4: // Vector3
                        state.Values[key] = stream.ReadVector3();
                        break;
                    case 5: // Quaternion
                        state.Values[key] = stream.ReadQuaternion();
                        break;
                }
            }

            return state;
        }
    }

    public class ClientInput
    {
        public int ClientId;
        public float Timestamp;
        public Dictionary<int, InputState> Inputs;

        public byte[] Serialize()
        {
            NetworkStream stream = new NetworkStream();
            stream.WriteInt(ClientId);
            stream.WriteFloat(Timestamp);
            stream.WriteInt(Inputs.Count);

            foreach (var kvp in Inputs)
            {
                stream.WriteInt(kvp.Key);
                byte[] inputStateData = kvp.Value.Serialize();
                stream.WriteInt(inputStateData.Length);
                foreach (var b in inputStateData)
                {
                    stream.WriteBool(b == 1);
                }
            }

            return stream.ToArray();
        }

        public static ClientInput Deserialize(byte[] data)
        {
            NetworkStream stream = new NetworkStream();
            stream.FromArray(data);

            ClientInput input = new ClientInput
            {
                ClientId = stream.ReadInt(),
                Timestamp = stream.ReadFloat(),
                Inputs = new Dictionary<int, InputState>()
            };

            int inputCount = stream.ReadInt();
            for (int i = 0; i < inputCount; i++)
            {
                int viewId = stream.ReadInt();
                int inputStateLength = stream.ReadInt();
                byte[] inputStateData = new byte[inputStateLength];

                for (int j = 0; j < inputStateLength; j++)
                {
                    inputStateData[j] = (byte)(stream.ReadBool() ? 1 : 0);
                }

                input.Inputs[viewId] = InputState.Deserialize(inputStateData);
            }

            return input;
        }
    }

    public class ConnectionAccepted
    {
        public int ClientId;
        public float ServerTime;

        public byte[] Serialize()
        {
            NetworkStream stream = new NetworkStream();
            stream.WriteInt(ClientId);
            stream.WriteFloat(ServerTime);
            return stream.ToArray();
        }

        public static ConnectionAccepted Deserialize(byte[] data)
        {
            NetworkStream stream = new NetworkStream();
            stream.FromArray(data);

            return new ConnectionAccepted
            {
                ClientId = stream.ReadInt(),
                ServerTime = stream.ReadFloat()
            };
        }
    }

    public class SpawnViewData
    {
        public int ViewId;
        public int PrefabId;
        public int OwnerId;
        public ViewState InitialState;

        public byte[] Serialize()
        {
            NetworkStream stream = new NetworkStream();
            stream.WriteInt(ViewId);
            stream.WriteInt(PrefabId);
            stream.WriteInt(OwnerId);

            byte[] stateData = InitialState.Serialize();
            stream.WriteInt(stateData.Length);
            foreach (var b in stateData)
            {
                stream.WriteBool(b == 1);
            }

            return stream.ToArray();
        }

        public static SpawnViewData Deserialize(byte[] data)
        {
            NetworkStream stream = new NetworkStream();
            stream.FromArray(data);

            SpawnViewData spawnData = new SpawnViewData
            {
                ViewId = stream.ReadInt(),
                PrefabId = stream.ReadInt(),
                OwnerId = stream.ReadInt()
            };

            int stateLength = stream.ReadInt();
            byte[] stateData = new byte[stateLength];

            for (int i = 0; i < stateLength; i++)
            {
                stateData[i] = (byte)(stream.ReadBool() ? 1 : 0);
            }

            spawnData.InitialState = ViewState.Deserialize(stateData);

            return spawnData;
        }
    }

    public class RPCData
    {
        public int ViewId;
        public string MethodName;
        public byte[] Parameters;

        public byte[] Serialize()
        {
            NetworkStream stream = new NetworkStream();
            stream.WriteInt(ViewId);
            stream.WriteString(MethodName);

            stream.WriteInt(Parameters.Length);
            foreach (var b in Parameters)
            {
                stream.WriteBool(b == 1);
            }

            return stream.ToArray();
        }

        public static RPCData Deserialize(byte[] data)
        {
            NetworkStream stream = new NetworkStream();
            stream.FromArray(data);

            RPCData rpcData = new RPCData
            {
                ViewId = stream.ReadInt(),
                MethodName = stream.ReadString()
            };

            int paramLength = stream.ReadInt();
            rpcData.Parameters = new byte[paramLength];

            for (int i = 0; i < paramLength; i++)
            {
                rpcData.Parameters[i] = (byte)(stream.ReadBool() ? 1 : 0);
            }

            return rpcData;
        }
    }

    public enum PacketType
    {
        ConnectionRequest,
        ConnectionAccepted,
        ConnectionRejected,
        Disconnect,
        ClientConnected,
        ClientDisconnected,
        ViewSyncState,
        ClientInput,
        SpawnView,
        DestroyView,
        RPC
    }

    public class NetworkPacket
    {
        public PacketType Type;
        public byte[] Data;
        public EndPoint SenderEndPoint;

        public byte[] Serialize()
        {
            List<byte> result = new List<byte>();
            result.Add((byte)Type);

            if (Data != null)
            {
                result.AddRange(BitConverter.GetBytes(Data.Length));
                result.AddRange(Data);
            }
            else
            {
                result.AddRange(BitConverter.GetBytes(0));
            }

            return result.ToArray();
        }

        public static NetworkPacket Deserialize(byte[] data)
        {
            if (data.Length < 5) return null;

            PacketType type = (PacketType)data[0];
            int dataLength = BitConverter.ToInt32(data, 1);

            byte[] packetData = null;
            if (dataLength > 0)
            {
                packetData = new byte[dataLength];
                Array.Copy(data, 5, packetData, 0, dataLength);
            }

            return new NetworkPacket
            {
                Type = type,
                Data = packetData
            };
        }

        public string GetString()
        {
            if (Data == null) return string.Empty;
            return System.Text.Encoding.UTF8.GetString(Data);
        }

        public byte[] GetBytes()
        {
            return Data;
        }
    }

    // helper to serialize/deserialize RPC parameters
    public static class RPCSerializer
    {
        public static byte[] SerializeParameters(object[] parameters)
        {
            NetworkStream stream = new NetworkStream();
            stream.WriteInt(parameters?.Length ?? 0);

            if (parameters != null)
            {
                foreach (var param in parameters)
                {
                    if (param == null)
                    {
                        stream.WriteInt(-1); // null type code
                    }
                    else if (param is bool boolValue)
                    {
                        stream.WriteInt(0); // Type code
                        stream.WriteBool(boolValue);
                    }
                    else if (param is int intValue)
                    {
                        stream.WriteInt(1); // Type code
                        stream.WriteInt(intValue);
                    }
                    else if (param is float floatValue)
                    {
                        stream.WriteInt(2); // Type code
                        stream.WriteFloat(floatValue);
                    }
                    else if (param is string stringValue)
                    {
                        stream.WriteInt(3); // Type code
                        stream.WriteString(stringValue);
                    }
                    else if (param is Vector3 vector3Value)
                    {
                        stream.WriteInt(4); // Type code
                        stream.WriteVector3(vector3Value);
                    }
                    else if (param is Quaternion quaternionValue)
                    {
                        stream.WriteInt(5); // Type code
                        stream.WriteQuaternion(quaternionValue);
                    }
                    else
                    {
                        Debug.LogWarning($"Unsupported parameter type: {param.GetType()}");
                        stream.WriteInt(-1); // Null as a fallback
                    }
                }
            }

            return stream.ToArray();
        }

        public static object[] DeserializeParameters(byte[] data)
        {
            NetworkStream stream = new NetworkStream();
            stream.FromArray(data);

            int paramCount = stream.ReadInt();
            object[] parameters = new object[paramCount];

            for (int i = 0; i < paramCount; i++)
            {
                int typeCode = stream.ReadInt();

                switch (typeCode)
                {
                    case -1: // null
                        parameters[i] = null;
                        break;
                    case 0: // bool
                        parameters[i] = stream.ReadBool();
                        break;
                    case 1: // int
                        parameters[i] = stream.ReadInt();
                        break;
                    case 2: // float
                        parameters[i] = stream.ReadFloat();
                        break;
                    case 3: // string
                        parameters[i] = stream.ReadString();
                        break;
                    case 4: // Vector3
                        parameters[i] = stream.ReadVector3();
                        break;
                    case 5: // Quaternion
                        parameters[i] = stream.ReadQuaternion();
                        break;
                    default:
                        parameters[i] = null;
                        break;
                }
            }

            return parameters;
        }
    }

    #endregion
}