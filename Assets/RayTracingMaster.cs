using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class RayTracingMaster : MonoBehaviour
{

    public ComputeShader RayTracingShader;
    public Texture SkyboxTexture;
    public Light DirectionalLight;

    public Vector2 SphereRadius = new Vector2(5, 30);
    public uint SpheresMax = 10000;
    public float SpherePlacementRadius = 100.0f;
    public int SphereSeed = 1223832719;

    private ComputeBuffer _sphereBuffer;
    private RenderTexture _target;
    private RenderTexture _converged;
    private Camera _camera;
    private uint _currentSample = 0;
    private Material _addMaterial;
    private static bool _meshObjectsNeedRebuilding = false;
    private static List<RayTracingObject> _rayTracingObjects = new List<RayTracingObject>();


    public struct Sphere
    {
        public Vector3 position { get; set; }
        public float radius { get; set; }
        public Vector3 albedo { get; set; }
        public Vector3 specular { get; set; }
        public Vector3 emission { get; set; }
        public float smoothness { get; set; }
    };

    struct MeshObject
    {
        public Matrix4x4 localToWorldMatrix;
        public int indices_offset;
        public int indices_count;
    }

    private static List<MeshObject> _meshObjects = new List<MeshObject>();
    private static List<Vector3> _vertices = new List<Vector3>();
    private static List<int> _indices = new List<int>();
    private ComputeBuffer _meshObjectBuffer;
    private ComputeBuffer _vertexBuffer;
    private ComputeBuffer _indexBuffer;


    private void Awake()
    {
        _camera = GetComponent<Camera>();
    }

    public static void RegisterObject(RayTracingObject obj)
    {
        _rayTracingObjects.Add(obj);
        _meshObjectsNeedRebuilding = true;
    }

    public static void UnregisterObject(RayTracingObject obj)
    {
        _rayTracingObjects.Remove(obj);
        _meshObjectsNeedRebuilding = true;
    }

    private static void CreateComputeBuffer<T>(ref ComputeBuffer buffer, List<T> data, int stride)
        where T : struct
    {
        if (buffer != null)
        {
            //if no data or buffer doesn't match the given criteria release it
            if (data.Count == 0 || buffer.count != data.Count || buffer.stride != stride)
            {
                buffer.Release();
                buffer = null;
            }
        }
        if (data.Count != 0)
        {
            if (buffer == null)
            {
                buffer = new ComputeBuffer(data.Count, stride);
            }
            //Set data on the buffer
            buffer.SetData(data);
        }
    }
    private void SetComputeBuffer(string name, ComputeBuffer buffer)
    {
        if (buffer != null)
        {
            RayTracingShader.SetBuffer(0, name, buffer);
        }
    }

    private void RebuildMeshObjectBuffers()
    {
        if (!_meshObjectsNeedRebuilding)
            return;
        _meshObjectsNeedRebuilding = false;
        //reset render
        _currentSample = 0;
        //clear old lists
        _meshObjects.Clear();
        _vertices.Clear();
        _indices.Clear();

        //itterate throught all objects and add their data
        foreach(RayTracingObject obj in _rayTracingObjects)
        {
            Mesh mesh = obj.GetComponent<MeshFilter>().sharedMesh;

            //add vertex data
            int firstVertex = _vertices.Count;
            _vertices.AddRange(mesh.vertices);

            //Add index data - if the vertex buffer wasn't empty before, the indices need to be updated by adding the index offset
            int firstIndex = _indices.Count;
            var indices = mesh.GetIndices(0);
            _indices.AddRange(indices.Select(index => (index + firstVertex)));

            //Add the object
            _meshObjects.Add(new MeshObject()
            {
                localToWorldMatrix = obj.transform.localToWorldMatrix,
                indices_offset = firstIndex,
                indices_count = indices.Length
            });
        }
        CreateComputeBuffer(ref _meshObjectBuffer, _meshObjects, 72);
        CreateComputeBuffer(ref _vertexBuffer, _vertices, 12);
        CreateComputeBuffer(ref _indexBuffer, _indices, 4);
    }

    private void SetShaderParameters()
    {
        RayTracingShader.SetMatrix("_CameraToWorld", _camera.cameraToWorldMatrix);
        RayTracingShader.SetMatrix("_CameraInverseProjection", _camera.projectionMatrix.inverse);
        RayTracingShader.SetTexture(0, "_SkyboxTexture", SkyboxTexture);
        RayTracingShader.SetVector("_PixelOffset", new Vector2(Random.value, Random.value));
        Vector3 l = DirectionalLight.transform.forward;
        RayTracingShader.SetVector("_DirectionalLight", new Vector4(l.x, l.y, l.z, DirectionalLight.intensity));
        RayTracingShader.SetFloat("_Seed", Random.value);

        SetComputeBuffer("_Spheres", _sphereBuffer);
        SetComputeBuffer("_MeshObjects", _meshObjectBuffer);
        SetComputeBuffer("_Vertices", _vertexBuffer);
        SetComputeBuffer("_Indices", _indexBuffer);
    }

    private void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        RebuildMeshObjectBuffers();
        SetShaderParameters();
        Render(destination);
    }

    private void Render(RenderTexture destination)
    {
        InitRenderTexture();
        

        RayTracingShader.SetTexture(0, "Result", _target);
        int threadGroupsX = Mathf.CeilToInt(Screen.width / 8.0f);
        int threadGroupsY = Mathf.CeilToInt(Screen.height/ 8.0f);
        RayTracingShader.Dispatch(0, threadGroupsX, threadGroupsY, 1);

        if (_addMaterial == null)
            _addMaterial = new 
                Material(Shader.Find("Hidden/AntiAliasing"));
        _addMaterial.SetFloat("_Sample", _currentSample);

        Graphics.Blit(_target, _converged, _addMaterial);
        Graphics.Blit(_converged, destination);
        _currentSample++;

    }

    private void InitRenderTexture()
    {
        if(_target == null || _target.width != Screen.width || _target.height != Screen.height)
        {
            if (_target != null)
            {
                _target.Release();
                _converged.Release();
            }
            _target = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            _converged = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            _target.enableRandomWrite = true;
            _currentSample = 0;
            _target.Create();
            _converged.Create();
        }
    }

    private void SetUpScene()
    {
        Random.InitState(SphereSeed);
        List<Sphere> spheres = new List<Sphere>();
        //add random number of spheres
        for (int i = 0; i < SpheresMax; i++)
        {
            Sphere sphere = new Sphere();

            //Radius and radius
            sphere.radius = SphereRadius.x + Random.value * (SphereRadius.y - SphereRadius.x);
            Vector2 randomPos = Random.insideUnitCircle * SpherePlacementRadius;
            sphere.position = new Vector3(randomPos.x, sphere.radius, randomPos.y);

            //reject intersecting spheres
            foreach (Sphere other in spheres)
            {
                float minDist = sphere.radius + other.radius;
                if (Vector3.SqrMagnitude(sphere.position - other.position) < minDist * minDist)
                {
                    goto SkipSphere;
                }
            }
            Color color = Random.ColorHSV();
            float chance = Random.value;
            if (chance < .8) {
                bool metal = chance < 0.4f;
                sphere.albedo = metal ? Vector3.zero : new Vector3(color.r, color.g, color.b);
                sphere.specular = metal ? new Vector3(color.r, color.g, color.b) : Vector3.one * .04f;
                sphere.smoothness = Random.value;
            }
            else {
                Color emission = Random.ColorHSV(0, 1, 0, 1, 3.0f, 8.0f);
                sphere.emission = new Vector3(emission.r, emission.g, emission.b);
            }
            //add sphere to list
            spheres.Add(sphere);
        SkipSphere:
            continue;

        }
        _sphereBuffer = new ComputeBuffer(spheres.Count, 56);
        _sphereBuffer.SetData(spheres);
    }

    private void OnEnable()
    {
        _currentSample = 0;
        SetUpScene();
    }

    private void OnDisable()
    {
        if (_sphereBuffer != null)
        {
            _sphereBuffer.Release();
            _meshObjectBuffer.Release();
            _vertexBuffer.Release();
            _indexBuffer.Release();
        }
    }

    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        if (DirectionalLight.transform.hasChanged)
        {
            _currentSample = 0;
            DirectionalLight.transform.hasChanged = false;
        }
        if (_camera.transform.hasChanged)
        {
            _currentSample = 0;
            _camera.transform.hasChanged = false;
        }
    }
}
