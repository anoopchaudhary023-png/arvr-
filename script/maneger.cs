using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Professional AR Chair Placement and Interaction Controller.
/// Features:
/// - Real-world automatic bounding box centering and life-size scaling (0.95m height).
/// - Procedural AR placement reticle indicating detected floor surface.
/// - Full gesture controls: tap to place/move, two-finger pinch to scale, twist/drag to rotate.
/// - Full Unity Editor / Play Mode testing with mouse (left-click place, right-click rotate, scroll scale).
/// - URP shader auto-compatibility to prevent magenta/pink textures.
/// - Executive chair finish customizer (Obsidian Black, Saddle Brown, Oxblood, Slate Grey, Arctic White).
/// - Modern in-game AR HUD with status pills, 100% life-size reset, 45° rotation buttons, and plane toggle.
/// </summary>
[RequireComponent(typeof(ARRaycastManager))]
public class ChairPlacer : MonoBehaviour
{
    [Header("Model Configuration")]
    [SerializeField]
    [Tooltip("The chair prefab to spawn on the floor.")]
    private GameObject chairPrefab;

    [SerializeField]
    [Tooltip("Real-world target height in meters (standard office/executive chair is ~0.95m).")]
    private float realWorldHeightMeters = 0.95f;

    [SerializeField]
    [Tooltip("Initial user scale multiplier.")]
    private float chairScale = 1.0f;

    [SerializeField]
    [Tooltip("Rotation sensitivity when dragging with finger.")]
    private float rotationSpeed = 0.35f;

    [Header("AR Components")]
    private ARRaycastManager arRaycastManager;
    private ARPlaneManager arPlaneManager;
    private static List<ARRaycastHit> hits = new List<ARRaycastHit>();

    // Spawned instance & reticle
    private GameObject spawnedChair;
    private GameObject placementReticle;
    private Pose currentReticlePose;
    private bool reticlePoseIsValid = false;

    // Model base measurements
    private float baseCalculatedScale = 1.0f;
    private float currentScaleMultiplier = 1.0f;

    // Plane visibility
    private bool arePlanesVisible = true;

    // Status & UI
    private string statusMessage = "Point your camera at the floor and move slowly...";
    private GUIStyle headerStyle;
    private GUIStyle buttonStyle;
    private GUIStyle statusBadgeStyle;
    private GUIStyle helpStyle;
    private bool showHelpModal = false;

    // Color Finishes
    private int currentColorIndex = 0;
    private readonly struct ChairColorPreset
    {
        public readonly string Name;
        public readonly Color PrimaryColor;
        public readonly float Smoothness;
        public readonly float Metallic;

        public ChairColorPreset(string name, Color color, float smoothness, float metallic)
        {
            Name = name;
            PrimaryColor = color;
            Smoothness = smoothness;
            Metallic = metallic;
        }
    }

    private readonly ChairColorPreset[] colorPresets = new ChairColorPreset[]
    {
        new ChairColorPreset("Obsidian Black", new Color(0.12f, 0.12f, 0.13f), 0.65f, 0.2f),
        new ChairColorPreset("Saddle Brown", new Color(0.48f, 0.26f, 0.12f), 0.45f, 0.1f),
        new ChairColorPreset("Oxblood Burgundy", new Color(0.35f, 0.12f, 0.16f), 0.50f, 0.15f),
        new ChairColorPreset("Slate Grey", new Color(0.32f, 0.35f, 0.39f), 0.55f, 0.2f),
        new ChairColorPreset("Polar White", new Color(0.92f, 0.92f, 0.94f), 0.60f, 0.15f)
    };

    // Touch interaction states
    private bool isTouchingPlacedChair = false;
    private float lastPinchDistance = 0f;

    void Awake()
    {
        arRaycastManager = GetComponent<ARRaycastManager>();
        arPlaneManager = GetComponent<ARPlaneManager>();

        // Auto-resolve prefab if not linked
        if (chairPrefab == null)
        {
            chairPrefab = Resources.Load<GameObject>("chair");
            if (chairPrefab == null)
            {
                Debug.LogWarning("[ChairPlacer] chairPrefab not assigned. Will attempt to locate in scene or resources.");
            }
        }

        // Create procedural AR placement reticle
        placementReticle = CreatePlacementReticle();
    }

#if UNITY_ANDROID
    private bool hasCameraPermission = false;
#endif

    IEnumerator Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera))
        {
            statusMessage = "Please allow Camera permission...";
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Camera);
        }
#endif

        yield return null;

        IEnumerator checkEnum = null;
        try
        {
            if (ARSession.state == ARSessionState.None || ARSession.state == ARSessionState.CheckingAvailability)
            {
                checkEnum = ARSession.CheckAvailability();
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("[ChairPlacer] ARSession availability exception: " + ex.Message);
        }

        if (checkEnum != null)
        {
            yield return checkEnum;
        }

        if (Application.isEditor)
        {
            statusMessage = "Editor Mode: Left-Click to place | Right-Click to rotate | Scroll to scale";
        }
    }

    void Update()
    {
        UpdateReticle();
        UpdateStatusMessage();

        // Handle Editor testing inputs
        if (Application.isEditor)
        {
            HandleEditorInput();
            return;
        }

        // Mobile touch inputs
        HandleTouchInput();
    }

    /// <summary>
    /// Creates a crisp procedural circular reticle that floats slightly above the detected floor.
    /// </summary>
    private GameObject CreatePlacementReticle()
    {
        GameObject reticle = new GameObject("AR_Placement_Reticle");
        LineRenderer lr = reticle.AddComponent<LineRenderer>();
        lr.useWorldSpace = false;
        lr.loop = true;
        lr.startWidth = 0.015f;
        lr.endWidth = 0.015f;

        int segments = 40;
        lr.positionCount = segments;
        float radius = 0.22f; // 22cm radius

        for (int i = 0; i < segments; i++)
        {
            float angle = i * Mathf.PI * 2f / segments;
            lr.SetPosition(i, new Vector3(Mathf.Cos(angle) * radius, 0.005f, Mathf.Sin(angle) * radius));
        }

        Shader lineShader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        if (lineShader != null)
        {
            lr.material = new Material(lineShader);
        }

        lr.startColor = new Color(0.18f, 0.68f, 1.0f, 0.9f);
        lr.endColor = new Color(0.18f, 0.68f, 1.0f, 0.9f);
        reticle.SetActive(false);
        return reticle;
    }

    /// <summary>
    /// Continuously raycasts from the screen center to track floor planes and update reticle.
    /// </summary>
    private void UpdateReticle()
    {
        if (spawnedChair != null)
        {
            if (placementReticle != null && placementReticle.activeSelf)
                placementReticle.SetActive(false);
            return;
        }

        Vector2 screenCenter = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

        if (arRaycastManager != null && arRaycastManager.Raycast(screenCenter, hits, TrackableType.PlaneWithinPolygon | TrackableType.PlaneWithinBounds))
        {
            reticlePoseIsValid = true;
            currentReticlePose = hits[0].pose;

            if (placementReticle != null)
            {
                placementReticle.SetActive(true);
                placementReticle.transform.position = currentReticlePose.position;
                placementReticle.transform.rotation = currentReticlePose.rotation;
            }
        }
        else
        {
            reticlePoseIsValid = false;
            if (placementReticle != null && placementReticle.activeSelf)
            {
                placementReticle.SetActive(false);
            }
        }
    }

    private void UpdateStatusMessage()
    {
        if (Application.isEditor) return;

        if (ARSession.state < ARSessionState.SessionTracking)
        {
            switch (ARSession.state)
            {
                case ARSessionState.Unsupported:
                    statusMessage = "AR is not supported on this phone.";
                    break;
                case ARSessionState.NeedsInstall:
                    statusMessage = "Please install 'Google Play Services for AR' from Play Store.";
                    break;
                case ARSessionState.Installing:
                    statusMessage = "Updating ARCore services... Please wait.";
                    break;
                case ARSessionState.CheckingAvailability:
                    statusMessage = "Connecting to AR Camera...";
                    break;
                default:
                    statusMessage = "Activating camera & AR tracking...";
                    break;
            }
            return;
        }

        if (spawnedChair != null)
        {
            statusMessage = "Chair Placed | Drag to move | Twist to rotate | Pinch to scale";
        }
        else if (reticlePoseIsValid)
        {
            statusMessage = "Floor detected! Tap anywhere to place the chair.";
        }
        else if (arPlaneManager != null && arPlaneManager.trackables.count > 0)
        {
            statusMessage = "Floor scanning in progress... Look at the floor.";
        }
        else
        {
            statusMessage = "Move your phone slowly around to scan the floor...";
        }
    }

    /// <summary>
    /// Touch interaction handler for Android devices.
    /// </summary>
    private void HandleTouchInput()
    {
        if (Input.touchCount == 0)
        {
            isTouchingPlacedChair = false;
            lastPinchDistance = 0f;
            return;
        }

        // Two-Finger Gestures (Pinch to scale & Twist to rotate)
        if (Input.touchCount == 2 && spawnedChair != null)
        {
            Touch t0 = Input.GetTouch(0);
            Touch t1 = Input.GetTouch(1);

            // Pinch to scale
            float currentDistance = Vector2.Distance(t0.position, t1.position);
            if (lastPinchDistance > 0f)
            {
                float delta = currentDistance - lastPinchDistance;
                float scaleFactor = 1.0f + (delta * 0.004f);
                SetChairScaleMultiplier(currentScaleMultiplier * scaleFactor);
            }
            lastPinchDistance = currentDistance;

            // Twist to rotate
            Vector2 prevDir = (t0.position - t0.deltaPosition) - (t1.position - t1.deltaPosition);
            Vector2 curDir = t0.position - t1.position;
            float angleDelta = Vector2.SignedAngle(prevDir, curDir);
            if (Mathf.Abs(angleDelta) > 0.05f)
            {
                spawnedChair.transform.Rotate(Vector3.up, -angleDelta, Space.World);
            }
            return;
        }

        lastPinchDistance = 0f;

        // Single Finger Interactions
        if (Input.touchCount == 1)
        {
            Touch touch = Input.GetTouch(0);

            // Don't interact if touching the bottom UI bar
            if (touch.position.y < Screen.height * 0.16f || (showHelpModal && touch.position.y > Screen.height * 0.2f))
            {
                return;
            }

            if (touch.phase == TouchPhase.Began)
            {
                // Check if user tapped directly on or near the placed chair to start dragging it
                if (spawnedChair != null)
                {
                    Ray ray = Camera.main != null ? Camera.main.ScreenPointToRay(touch.position) : new Ray();
                    if (Physics.Raycast(ray, out RaycastHit hitInfo))
                    {
                        if (hitInfo.transform.IsChildOf(spawnedChair.transform) || hitInfo.transform == spawnedChair.transform)
                        {
                            isTouchingPlacedChair = true;
                            return;
                        }
                    }
                }

                // If chair is not placed yet, place it on the tapped plane or reticle
                if (spawnedChair == null)
                {
                    if (arRaycastManager != null && arRaycastManager.Raycast(touch.position, hits, TrackableType.PlaneWithinPolygon | TrackableType.PlaneWithinBounds))
                    {
                        PlaceChair(hits[0].pose.position, hits[0].pose.rotation);
                    }
                    else if (reticlePoseIsValid)
                    {
                        PlaceChair(currentReticlePose.position, currentReticlePose.rotation);
                    }
                }
            }
            else if (touch.phase == TouchPhase.Moved && spawnedChair != null)
            {
                if (isTouchingPlacedChair)
                {
                    // Drag chair across detected planes
                    if (arRaycastManager != null && arRaycastManager.Raycast(touch.position, hits, TrackableType.PlaneWithinPolygon | TrackableType.PlaneWithinBounds))
                    {
                        spawnedChair.transform.position = hits[0].pose.position;
                    }
                }
                else
                {
                    // Drag outside chair to rotate it smoothly
                    float deltaX = touch.deltaPosition.x;
                    if (Mathf.Abs(deltaX) > 0.3f)
                    {
                        spawnedChair.transform.Rotate(Vector3.up, -deltaX * rotationSpeed, Space.World);
                    }
                }
            }
            else if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled)
            {
                isTouchingPlacedChair = false;
            }
        }
    }

    /// <summary>
    /// Fallback for testing directly in the Unity Editor Play Mode without physical device.
    /// </summary>
    private void HandleEditorInput()
    {
        // Left Click to Place or Move
        if (Input.GetMouseButtonDown(0))
        {
            Ray ray = Camera.main != null ? Camera.main.ScreenPointToRay(Input.mousePosition) : new Ray(Vector3.up * 2f, Vector3.down);
            Plane floorPlane = new Plane(Vector3.up, Vector3.zero);
            if (floorPlane.Raycast(ray, out float enter))
            {
                Vector3 hitPoint = ray.GetPoint(enter);
                if (spawnedChair == null)
                {
                    PlaceChair(hitPoint, Quaternion.identity);
                }
                else
                {
                    spawnedChair.transform.position = hitPoint;
                }
            }
        }

        // Right Click Drag to Rotate
        if (Input.GetMouseButton(1) && spawnedChair != null)
        {
            float mouseX = Input.GetAxis("Mouse X");
            spawnedChair.transform.Rotate(Vector3.up, -mouseX * 5.0f, Space.World);
        }

        // Mouse Scroll Wheel to Scale
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) > 0.01f && spawnedChair != null)
        {
            SetChairScaleMultiplier(currentScaleMultiplier * (1.0f + scroll * 0.8f));
        }
    }

    /// <summary>
    /// Spawns the chair, centers its CAD offset, normalizes real-world dimensions, and applies materials.
    /// </summary>
    public void PlaceChair(Vector3 position, Quaternion rotation)
    {
        if (chairPrefab == null)
        {
            chairPrefab = Resources.Load<GameObject>("chair");
            if (chairPrefab == null)
            {
                Debug.LogError("[ChairPlacer] Cannot place chair: chairPrefab is null!");
                statusMessage = "Error: Chair model prefab missing!";
                return;
            }
        }

        // Instantiate
        spawnedChair = Instantiate(chairPrefab, position, rotation);
        spawnedChair.name = "PlacedChair_AR";
        spawnedChair.SetActive(true);

        // Normalize CAD offsets, bounds, and life-size height
        NormalizeAndCenterModel(spawnedChair, realWorldHeightMeters);

        // Ensure materials are compatible with URP
        EnsureUrpMaterials(spawnedChair);

        // Apply starting color preset
        ApplyColorPreset(currentColorIndex);

        // Auto-orient chair towards camera
        if (Camera.main != null)
        {
            Vector3 lookDir = Camera.main.transform.position - spawnedChair.transform.position;
            lookDir.y = 0;
            if (lookDir.sqrMagnitude > 0.001f)
            {
                spawnedChair.transform.rotation = Quaternion.LookRotation(-lookDir.normalized, Vector3.up);
            }
        }

        currentScaleMultiplier = 1.0f;

        // Hide reticle & floor visualization for clean presentation
        if (placementReticle != null)
            placementReticle.SetActive(false);

        SetPlaneVisualsActive(false);
        arePlanesVisible = false;
    }

    /// <summary>
    /// Automatic normalization engine:
    /// Measures child renderers, shifts geometry so horizontal center is at (0, 0),
    /// places the bottom of the wheels precisely at Y = 0 (detected floor surface),
    /// and scales uniformly so the chair matches true human real-world height.
    /// </summary>
    private void NormalizeAndCenterModel(GameObject rootObj, float targetHeightMeters)
    {
        Renderer[] renderers = rootObj.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return;

        // Compute combined bounding box across all sub-meshes
        Bounds combinedBounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            combinedBounds.Encapsulate(renderers[i].bounds);
        }

        // 1. Calculate height and determine initial scale normalization
        float currentHeight = combinedBounds.size.y;
        if (currentHeight > 0.001f)
        {
            baseCalculatedScale = targetHeightMeters / currentHeight;
            rootObj.transform.localScale = Vector3.one * (baseCalculatedScale * chairScale);
        }
        else
        {
            baseCalculatedScale = 1.0f;
        }

        // 2. Recompute bounds after initial scaling
        combinedBounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            combinedBounds.Encapsulate(renderers[i].bounds);
        }

        // 3. We want rootObj.transform.position to be at the bottom-center of the chair
        Vector3 bottomCenter = new Vector3(combinedBounds.center.x, combinedBounds.min.y, combinedBounds.center.z);
        Vector3 offset = bottomCenter - rootObj.transform.position;

        // Shift child transforms by -offset so the visual model aligns with the root pivot
        foreach (Transform child in rootObj.transform)
        {
            child.position -= offset;
        }

        // 4. Configure BoxCollider around normalized chair for raycasting and selection
        BoxCollider boxCol = rootObj.GetComponent<BoxCollider>();
        if (boxCol == null) boxCol = rootObj.AddComponent<BoxCollider>();
        boxCol.center = new Vector3(0, targetHeightMeters * 0.5f, 0);
        boxCol.size = new Vector3(targetHeightMeters * 0.7f, targetHeightMeters, targetHeightMeters * 0.7f);
    }

    /// <summary>
    /// Safeguards against pink/magenta shaders by upgrading materials to URP Lit.
    /// </summary>
    private void EnsureUrpMaterials(GameObject chair)
    {
        Shader urpLit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Mobile/Diffuse") ?? Shader.Find("Standard");
        if (urpLit == null) return;

        foreach (Renderer r in chair.GetComponentsInChildren<Renderer>())
        {
            foreach (Material m in r.materials)
            {
                if (m == null) continue;
                if (m.shader == null || !m.shader.name.Contains("Universal"))
                {
                    Color orig = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : (m.HasProperty("_Color") ? m.color : Color.gray);
                    m.shader = urpLit;
                    if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", orig);
                    else if (m.HasProperty("_Color")) m.color = orig;
                }
            }
        }
    }

    /// <summary>
    /// Sets user scale multiplier clamped between 25% and 250% of real-world size.
    /// </summary>
    public void SetChairScaleMultiplier(float multiplier)
    {
        currentScaleMultiplier = Mathf.Clamp(multiplier, 0.25f, 2.5f);
        if (spawnedChair != null)
        {
            spawnedChair.transform.localScale = Vector3.one * (baseCalculatedScale * chairScale * currentScaleMultiplier);
        }
    }

    /// <summary>
    /// Resets chair scale to exact 100% life-size (1:1 scale).
    /// </summary>
    public void ResetToLifeSize()
    {
        SetChairScaleMultiplier(1.0f);
        statusMessage = "Reset to 100% Life-Size (1:1 Real Scale)";
    }

    /// <summary>
    /// Rotates the chair by a fixed angle (e.g. +45° or -45°).
    /// </summary>
    public void RotateChairBy(float angleDegrees)
    {
        if (spawnedChair != null)
        {
            spawnedChair.transform.Rotate(Vector3.up, angleDegrees, Space.World);
        }
    }

    /// <summary>
    /// Removes current chair and allows user to scan and place in a new location.
    /// </summary>
    public void ResetPlacement()
    {
        if (spawnedChair != null)
        {
            Destroy(spawnedChair);
            spawnedChair = null;
        }

        SetPlaneVisualsActive(true);
        arePlanesVisible = true;
        statusMessage = "Chair removed. Move phone to scan floor again.";
    }

    /// <summary>
    /// Cycles through executive leather and fabric finishes.
    /// </summary>
    public void CycleColorPreset()
    {
        currentColorIndex = (currentColorIndex + 1) % colorPresets.Length;
        ApplyColorPreset(currentColorIndex);
    }

    private void ApplyColorPreset(int index)
    {
        if (spawnedChair == null || index < 0 || index >= colorPresets.Length) return;

        ChairColorPreset preset = colorPresets[index];

        foreach (Renderer r in spawnedChair.GetComponentsInChildren<Renderer>())
        {
            foreach (Material m in r.materials)
            {
                if (m == null) continue;

                // Executive or primary seat/back materials
                if (m.name.Contains("Executive") || m.name.Contains("seat") || m.name.Contains("48") || m.name.Contains("cushion"))
                {
                    if (m.HasProperty("_BaseColor"))
                        m.SetColor("_BaseColor", preset.PrimaryColor);
                    else if (m.HasProperty("_Color"))
                        m.color = preset.PrimaryColor;

                    if (m.HasProperty("_Smoothness"))
                        m.SetFloat("_Smoothness", preset.Smoothness);
                    if (m.HasProperty("_Metallic"))
                        m.SetFloat("_Metallic", preset.Metallic);
                }
            }
        }

        statusMessage = "Color: " + preset.Name;
    }

    /// <summary>
    /// Toggles AR plane polygon visualization mesh on and off.
    /// </summary>
    public void TogglePlaneVisuals()
    {
        arePlanesVisible = !arePlanesVisible;
        SetPlaneVisualsActive(arePlanesVisible);
        statusMessage = arePlanesVisible ? "Floor scan mesh: ON" : "Floor scan mesh: OFF (Photo Mode)";
    }

    private void SetPlaneVisualsActive(bool active)
    {
        if (arPlaneManager == null) return;
        foreach (var plane in arPlaneManager.trackables)
        {
            plane.gameObject.SetActive(active);
        }
    }

    /// <summary>
    /// High-DPI modern responsive UI overlay.
    /// </summary>
    void OnGUI()
    {
        InitGuiStyles();

        float sw = Screen.width;
        float sh = Screen.height;

        // 1. Top Status Pill
        float pillW = Mathf.Min(sw * 0.92f, 650f);
        float pillH = Mathf.Max(sh * 0.055f, 44f);
        float pillX = (sw - pillW) * 0.5f;
        float pillY = Mathf.Max(sh * 0.04f, 25f);

        GUI.Box(new Rect(pillX, pillY, pillW, pillH), statusMessage, statusBadgeStyle);

        // 2. Help Button (Top Right)
        float helpSize = Mathf.Max(sh * 0.05f, 40f);
        if (GUI.Button(new Rect(sw - helpSize - 20f, pillY, helpSize, helpSize), "❓", buttonStyle))
        {
            showHelpModal = !showHelpModal;
        }

        // 3. Help Modal Drawer if active
        if (showHelpModal)
        {
            float modalW = Mathf.Min(sw * 0.88f, 500f);
            float modalH = Mathf.Min(sh * 0.45f, 320f);
            float modalX = (sw - modalW) * 0.5f;
            float modalY = pillY + pillH + 15f;

            string helpText = "<b>CHAIR PROJECTION AR GUIDE</b>\n\n" +
                              "• <b>Scan:</b> Slowly pan phone until blue ring appears on floor.\n" +
                              "• <b>Place:</b> Tap anywhere on the floor to drop the chair.\n" +
                              "• <b>Move:</b> Drag chair across the room with one finger.\n" +
                              "• <b>Rotate:</b> Twist with two fingers, drag, or tap ⟲ ⟳.\n" +
                              "• <b>Scale:</b> Pinch to resize or tap '1:1' for true real size.\n" +
                              "• <b>Finish:</b> Tap '🎨' to change leather color.\n\n" +
                              "<i>Tap [Close] or ❓ to dismiss.</i>";

            GUI.Box(new Rect(modalX, modalY, modalW, modalH), helpText, helpStyle);
            if (GUI.Button(new Rect(modalX + modalW - 90f, modalY + modalH - 45f, 80f, 35f), "Close", buttonStyle))
            {
                showHelpModal = false;
            }
        }

        // 4. Bottom Control Toolbar (if chair is placed)
        if (spawnedChair != null)
        {
            float barH = Mathf.Max(sh * 0.07f, 52f);
            float barY = sh - barH - 25f;

            float btnGap = 8f;
            int btnCount = 6;
            float totalGap = (btnCount - 1) * btnGap;
            float maxBarW = Mathf.Min(sw * 0.94f, 620f);
            float btnW = (maxBarW - totalGap) / btnCount;
            float startX = (sw - maxBarW) * 0.5f;

            // Button 1: Reset 1:1 Life Size
            string sizeLabel = Mathf.RoundToInt(currentScaleMultiplier * 100f) + "%";
            if (GUI.Button(new Rect(startX + 0 * (btnW + btnGap), barY, btnW, barH), "📏\n" + sizeLabel, buttonStyle))
            {
                ResetToLifeSize();
            }

            // Button 2: Rotate Left 45
            if (GUI.Button(new Rect(startX + 1 * (btnW + btnGap), barY, btnW, barH), "⟲\n-45°", buttonStyle))
            {
                RotateChairBy(-45f);
            }

            // Button 3: Rotate Right 45
            if (GUI.Button(new Rect(startX + 2 * (btnW + btnGap), barY, btnW, barH), "⟳\n+45°", buttonStyle))
            {
                RotateChairBy(45f);
            }

            // Button 4: Cycle Finish / Color
            string colorName = colorPresets[currentColorIndex].Name.Split(' ')[0];
            if (GUI.Button(new Rect(startX + 3 * (btnW + btnGap), barY, btnW, barH), "🎨\n" + colorName, buttonStyle))
            {
                CycleColorPreset();
            }

            // Button 5: Toggle Floor Plane Mesh
            string planeIcon = arePlanesVisible ? "👁️\nGrid" : "📷\nClean";
            if (GUI.Button(new Rect(startX + 4 * (btnW + btnGap), barY, btnW, barH), planeIcon, buttonStyle))
            {
                TogglePlaneVisuals();
            }

            // Button 6: Delete & Place Again
            if (GUI.Button(new Rect(startX + 5 * (btnW + btnGap), barY, btnW, barH), "🗑️\nReset", buttonStyle))
            {
                ResetPlacement();
            }
        }
    }

    private void InitGuiStyles()
    {
        if (statusBadgeStyle != null) return;

        int fontSizeSmall = Mathf.Max(Screen.height / 52, 14);
        int fontSizeMed = Mathf.Max(Screen.height / 46, 16);

        // Status Badge Style
        statusBadgeStyle = new GUIStyle(GUI.skin.box)
        {
            fontSize = fontSizeMed,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            wordWrap = true,
            padding = new RectOffset(16, 16, 8, 8)
        };
        statusBadgeStyle.normal.textColor = new Color(0.95f, 0.98f, 1.0f);
        statusBadgeStyle.normal.background = MakeSolidTexture(new Color(0.08f, 0.10f, 0.14f, 0.88f));

        // Button Style
        buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = fontSizeSmall,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            padding = new RectOffset(4, 4, 4, 4)
        };
        buttonStyle.normal.textColor = Color.white;
        buttonStyle.normal.background = MakeSolidTexture(new Color(0.12f, 0.16f, 0.22f, 0.92f));
        buttonStyle.active.background = MakeSolidTexture(new Color(0.18f, 0.58f, 0.92f, 0.95f));

        // Help Modal Style
        helpStyle = new GUIStyle(GUI.skin.box)
        {
            fontSize = fontSizeSmall,
            alignment = TextAnchor.UpperLeft,
            wordWrap = true,
            richText = true,
            padding = new RectOffset(18, 18, 18, 18)
        };
        helpStyle.normal.textColor = new Color(0.94f, 0.96f, 0.98f);
        helpStyle.normal.background = MakeSolidTexture(new Color(0.06f, 0.08f, 0.12f, 0.95f));
    }

    private Texture2D MakeSolidTexture(Color col)
    {
        Texture2D tex = new Texture2D(1, 1);
        tex.SetPixel(0, 0, col);
        tex.Apply();
        return tex;
    }
}

/// <summary>
/// Alias class for backward compatibility with inspector scripts named 'maneger'.
/// </summary>
public class maneger : ChairPlacer
{
}
