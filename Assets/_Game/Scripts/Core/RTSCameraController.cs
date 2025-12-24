using UnityEngine;
using System.Collections;

public class RTSCameraController : MonoBehaviour
{
    [Header("--- 移动设置 ---")]
    public float panSpeed = 40f;
    public float panBorderThickness = 15f;
    public bool useEdgeScrolling = true;

    [Header("--- 缩放设置 ---")]
    public float zoomSpeed = 20f;
    public float minHeight = 10f;
    public float maxHeight = 80f;
    public float zoomSmoothness = 5f;

    [Header("--- 旋转设置 ---")]
    public float rotationStep = 5f;
    public float rotationSmoothness = 10f;

    [Header("--- 旋转吸附 ---")]
    public bool useRotationSnapping = true;
    public float snapAngle = 45f;

    private Vector3 _targetPosition;
    private float _targetZoomHeight;
    private float _currentZoomHeight;
    private Quaternion _targetRotation;

    private float _initialPitch = 60f;

    private float _minX, _maxX, _minZ, _maxZ;
    private bool _isInitialized = false;

    // [新增] 相机移动边界缓冲 (允许移出地图多少米)
    private float _camPadding = 50f;

    IEnumerator Start()
    {
        _targetPosition = transform.position;
        _targetZoomHeight = transform.position.y;
        _currentZoomHeight = transform.position.y;
        _targetRotation = transform.rotation;

        if (transform.rotation.eulerAngles.x < 30)
        {
            transform.rotation = Quaternion.Euler(60f, 0f, 0f);
            _targetRotation = transform.rotation;
        }
        _initialPitch = transform.eulerAngles.x;

        while (MapGenerator.Instance == null || !MapGenerator.Instance.IsInitialized)
        {
            yield return null;
        }

        InitCameraPosition();
    }

    void InitCameraPosition()
    {
        if (MapGenerator.Instance != null)
        {
            float width = MapGenerator.Instance.width;
            float height = MapGenerator.Instance.height;

            // [修改] 扩大相机移动范围，允许看边缘
            _minX = -_camPadding;
            _maxX = width + _camPadding;
            _minZ = -_camPadding;
            _maxZ = height + _camPadding;

            _currentZoomHeight = 60f;
            _targetZoomHeight = 60f;

            // [核心] 跳转到 MapGenerator 计算好的随机出生点
            Vector2Int spawn = MapGenerator.Instance.PlayerSpawnPoint;

            _targetPosition.x = spawn.x;
            // 俯视角补偿
            _targetPosition.z = spawn.y - 40f;

            transform.position = new Vector3(_targetPosition.x, _currentZoomHeight, _targetPosition.z);
            _isInitialized = true;
        }
    }

    void Update()
    {
        if (!Application.isFocused || !_isInitialized) return;

        HandleMovement();
        HandleZoom();
        HandleRotation();

        Vector3 finalPos = _targetPosition;
        _currentZoomHeight = Mathf.Lerp(_currentZoomHeight, _targetZoomHeight, Time.deltaTime * zoomSmoothness);
        finalPos.y = _currentZoomHeight;
        transform.position = finalPos;

        transform.rotation = Quaternion.Slerp(transform.rotation, _targetRotation, Time.deltaTime * rotationSmoothness);
    }

    void HandleMovement()
    {
        Vector3 pos = _targetPosition;
        Vector3 mousePos = Input.mousePosition;

        Vector3 forward = transform.forward;
        forward.y = 0;
        forward.Normalize();

        Vector3 right = transform.right;
        right.y = 0;
        right.Normalize();

        if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) pos += forward * panSpeed * Time.deltaTime;
        if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) pos -= forward * panSpeed * Time.deltaTime;
        if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) pos += right * panSpeed * Time.deltaTime;
        if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) pos -= right * panSpeed * Time.deltaTime;

        if (useEdgeScrolling)
        {
            bool isInsideScreen = (mousePos.x >= 0 && mousePos.x <= Screen.width && mousePos.y >= 0 && mousePos.y <= Screen.height);
            if (isInsideScreen)
            {
                if (mousePos.y >= Screen.height - panBorderThickness) pos += forward * panSpeed * Time.deltaTime;
                if (mousePos.y <= panBorderThickness) pos -= forward * panSpeed * Time.deltaTime;
                if (mousePos.x >= Screen.width - panBorderThickness) pos += right * panSpeed * Time.deltaTime;
                if (mousePos.x <= panBorderThickness) pos -= right * panSpeed * Time.deltaTime;
            }
        }

        if (MapGenerator.Instance != null)
        {
            pos.x = Mathf.Clamp(pos.x, _minX, _maxX);
            pos.z = Mathf.Clamp(pos.z, _minZ, _maxZ);
        }

        _targetPosition.x = pos.x;
        _targetPosition.z = pos.z;
    }

    void HandleZoom()
    {
        if (Input.mousePosition.x >= 0 && Input.mousePosition.x <= Screen.width &&
            Input.mousePosition.y >= 0 && Input.mousePosition.y <= Screen.height)
        {
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            _targetZoomHeight -= scroll * zoomSpeed * 10f;
            _targetZoomHeight = Mathf.Clamp(_targetZoomHeight, minHeight, maxHeight);
        }
    }

    void HandleRotation()
    {
        if (Input.GetMouseButton(2))
        {
            float rotateAmount = Input.GetAxis("Mouse X") * 2f;
            _targetRotation = Quaternion.Euler(0, rotateAmount, 0) * _targetRotation;
        }

        if (Input.GetMouseButtonUp(2) && useRotationSnapping)
        {
            Vector3 currentEuler = _targetRotation.eulerAngles;
            float y = currentEuler.y;
            float snappedY = Mathf.Round(y / snapAngle) * snapAngle;
            _targetRotation = Quaternion.Euler(_initialPitch, snappedY, 0f);
        }

        if (Input.GetKeyDown(KeyCode.Q))
        {
            _targetRotation = Quaternion.Euler(0, rotationStep, 0) * _targetRotation;
        }
        else if (Input.GetKey(KeyCode.Q))
        {
            float continuousSpeed = rotationStep * 10f;
            _targetRotation = Quaternion.Euler(0, continuousSpeed * Time.deltaTime, 0) * _targetRotation;
        }

        if (Input.GetKeyDown(KeyCode.E))
        {
            _targetRotation = Quaternion.Euler(0, -rotationStep, 0) * _targetRotation;
        }
        else if (Input.GetKey(KeyCode.E))
        {
            float continuousSpeed = rotationStep * 10f;
            _targetRotation = Quaternion.Euler(0, -continuousSpeed * Time.deltaTime, 0) * _targetRotation;
        }
    }
}