#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

public sealed class HierarchyReplacementTool : EditorWindow
{
    private const string ReplacementGroupSuffix = "_replaced_group";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    [SerializeField] private Transform root;
    [SerializeField] private Vector2 scrollPosition;

    // General replacement
    [SerializeField] private string replacementSearch = string.Empty;
    [SerializeField] private bool replacementUsesRegex;
    [SerializeField] private bool replacementCaseSensitive;
    [SerializeField] private bool replacementTopLevelOnly = true;
    [SerializeField] private GameObject replacementPrefab;
    [SerializeField] private bool replacementCopiesRotation = true;
    [SerializeField] private bool replacementCopiesWorldScale;
    [SerializeField] private bool replacementCopiesLayer = true;

    // Tree replacement
    [SerializeField] private string treeSearch = "tree";
    [SerializeField] private bool treeUsesRegex;
    [SerializeField] private bool treeCaseSensitive;
    [SerializeField] private bool treeTopLevelOnly = true;
    [SerializeField] private List<GameObject> treePrefabs = new List<GameObject>();
    [SerializeField] private int randomSeed = 12345;
    [SerializeField] private bool treeCopiesRotation = true;
    [SerializeField] private bool randomizeYaw = true;
    [SerializeField] private Vector2 yawRange = new Vector2(0.0f, 360.0f);
    [SerializeField] private bool treeCopiesWorldScale;
    [SerializeField] private bool randomizeUniformScale = true;
    [SerializeField] private Vector2 uniformScaleRange = new Vector2(0.85f, 1.20f);
    [SerializeField] private bool treeCopiesLayer = true;

    [SerializeField] private string statusMessage = string.Empty;
    [SerializeField] private MessageType statusType = MessageType.Info;

    [MenuItem("Tools/Hierarchy Replacement Tool")]
    private static void OpenWindow()
    {
        HierarchyReplacementTool window =
            GetWindow<HierarchyReplacementTool>("Hierarchy Replacement");

        window.minSize = new Vector2(430.0f, 620.0f);
        window.Show();
    }

    private void OnEnable()
    {
        if (root == null && Selection.activeTransform != null)
        {
            root = Selection.activeTransform;
        }
    }

    private void OnSelectionChange()
    {
        Repaint();
    }

    private void OnGUI()
    {
        DrawRootSection();

        EditorGUILayout.Space(8.0f);

        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        DrawGeneralReplacementSection();

        EditorGUILayout.Space(12.0f);

        DrawTreeReplacementSection();

        EditorGUILayout.Space(12.0f);

        if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            EditorGUILayout.HelpBox(statusMessage, statusType);
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawRootSection()
    {
        EditorGUILayout.LabelField("Search Root", EditorStyles.boldLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            root = (Transform)EditorGUILayout.ObjectField(
                new GUIContent(
                    "Root",
                    "All searches are restricted to this hierarchy. " +
                    "The selected GameObject is assigned automatically when the window first opens."),
                root,
                typeof(Transform),
                true);

            using (new EditorGUI.DisabledScope(Selection.activeTransform == null))
            {
                if (GUILayout.Button("Use Selection", GUILayout.Width(105.0f)))
                {
                    root = Selection.activeTransform;
                    SetStatus(
                        root != null
                            ? $"Root set to '{root.name}'."
                            : "No GameObject is selected.",
                        root != null ? MessageType.Info : MessageType.Warning);
                }
            }
        }

        if (root == null)
        {
            EditorGUILayout.HelpBox(
                "Select cib_site (or another hierarchy root), then click Use Selection.",
                MessageType.Warning);
        }
    }

    private void DrawGeneralReplacementSection()
    {
        EditorGUILayout.LabelField("Single-Prefab Replacement", EditorStyles.boldLabel);

        EditorGUILayout.HelpBox(
            "Replace & Deactivate searches the original hierarchy, clears the generated " +
            "replacement group if it already exists, recreates the prefab instances, " +
            "and deactivates the originals. The operation is Undo-compatible.",
            MessageType.None);

        replacementSearch = EditorGUILayout.TextField(
            new GUIContent("Search", "Matches GameObject names under Root."),
            replacementSearch);

        DrawSearchOptions(
            ref replacementUsesRegex,
            ref replacementCaseSensitive,
            ref replacementTopLevelOnly);

        replacementPrefab = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent("Candidate Prefab"),
            replacementPrefab,
            typeof(GameObject),
            false);

        replacementCopiesRotation = EditorGUILayout.Toggle(
            new GUIContent(
                "Copy World Rotation",
                "Use the original object's world rotation. Disable to preserve the prefab's rotation."),
            replacementCopiesRotation);

        replacementCopiesWorldScale = EditorGUILayout.Toggle(
            new GUIContent(
                "Copy World Scale",
                "Use the original object's world scale. Disable to preserve the prefab's authored scale."),
            replacementCopiesWorldScale);

        replacementCopiesLayer = EditorGUILayout.Toggle(
            new GUIContent(
                "Copy Layer Recursively",
                "Assign the original object's layer to the complete replacement hierarchy."),
            replacementCopiesLayer);

        DrawActionButtons(
            () => PreviewMatches(
                replacementSearch,
                replacementUsesRegex,
                replacementCaseSensitive,
                replacementTopLevelOnly),
            () => ReplaceWithSinglePrefab(),
            () => ToggleOriginalsAndReplacements(
                replacementSearch,
                replacementUsesRegex,
                replacementCaseSensitive,
                replacementTopLevelOnly),
            CanRunSingleReplacement());
    }

    private void DrawTreeReplacementSection()
    {
        EditorGUILayout.LabelField("Randomized Tree Replacement", EditorStyles.boldLabel);

        treeSearch = EditorGUILayout.TextField(
            new GUIContent("Search", "Matches tree GameObject names under Root."),
            treeSearch);

        DrawSearchOptions(
            ref treeUsesRegex,
            ref treeCaseSensitive,
            ref treeTopLevelOnly);

        DrawTreePrefabList();

        EditorGUILayout.Space(3.0f);

        randomSeed = EditorGUILayout.IntField(
            new GUIContent(
                "Random Seed",
                "The same seed and hierarchy produce the same prefab, yaw, and scale selections."),
            randomSeed);

        treeCopiesRotation = EditorGUILayout.Toggle(
            new GUIContent(
                "Copy World Rotation",
                "Use the original object's world rotation before applying randomized yaw."),
            treeCopiesRotation);

        randomizeYaw = EditorGUILayout.Toggle(
            new GUIContent("Randomize Yaw", "Rotate each replacement around the world Y axis."),
            randomizeYaw);

        if (randomizeYaw)
        {
            yawRange = EditorGUILayout.Vector2Field(
                new GUIContent("Yaw Range"),
                yawRange);
        }

        treeCopiesWorldScale = EditorGUILayout.Toggle(
            new GUIContent(
                "Copy World Scale",
                "Use the original object's world scale as the base scale."),
            treeCopiesWorldScale);

        randomizeUniformScale = EditorGUILayout.Toggle(
            new GUIContent(
                "Random Uniform Scale",
                "Multiply the base world scale by a deterministic random value."),
            randomizeUniformScale);

        if (randomizeUniformScale)
        {
            uniformScaleRange = EditorGUILayout.Vector2Field(
                new GUIContent("Scale Range"),
                uniformScaleRange);
        }

        treeCopiesLayer = EditorGUILayout.Toggle(
            new GUIContent(
                "Copy Layer Recursively",
                "Assign the original object's layer to the complete replacement hierarchy."),
            treeCopiesLayer);

        DrawActionButtons(
            () => PreviewMatches(
                treeSearch,
                treeUsesRegex,
                treeCaseSensitive,
                treeTopLevelOnly),
            () => ReplaceTrees(),
            () => ToggleOriginalsAndReplacements(
                treeSearch,
                treeUsesRegex,
                treeCaseSensitive,
                treeTopLevelOnly),
            CanRunTreeReplacement());
    }

    private void DrawTreePrefabList()
    {
        EditorGUILayout.LabelField(
            new GUIContent(
                "Candidate Prefabs",
                "A deterministic random prefab is selected for each matched source object."),
            EditorStyles.label);

        EditorGUI.indentLevel++;

        int requestedCount = Mathf.Max(
            0,
            EditorGUILayout.IntField("Size", treePrefabs.Count));

        while (treePrefabs.Count < requestedCount)
        {
            treePrefabs.Add(null);
        }

        while (treePrefabs.Count > requestedCount)
        {
            treePrefabs.RemoveAt(treePrefabs.Count - 1);
        }

        for (int index = 0; index < treePrefabs.Count; index++)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                treePrefabs[index] = (GameObject)EditorGUILayout.ObjectField(
                    $"Element {index}",
                    treePrefabs[index],
                    typeof(GameObject),
                    false);

                if (GUILayout.Button("−", GUILayout.Width(24.0f)))
                {
                    treePrefabs.RemoveAt(index);
                    GUIUtility.ExitGUI();
                }
            }
        }

        if (GUILayout.Button("Add Prefab"))
        {
            treePrefabs.Add(null);
        }

        EditorGUI.indentLevel--;
    }

    private static void DrawSearchOptions(
        ref bool usesRegex,
        ref bool caseSensitive,
        ref bool topLevelOnly)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            usesRegex = EditorGUILayout.ToggleLeft(
                new GUIContent("Regex", "Interpret Search as a regular expression."),
                usesRegex,
                GUILayout.Width(70.0f));

            caseSensitive = EditorGUILayout.ToggleLeft(
                new GUIContent("Case Sensitive"),
                caseSensitive,
                GUILayout.Width(120.0f));

            topLevelOnly = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Top-Level Matches Only",
                    "If a matching object contains another matching object, replace only the parent match."),
                topLevelOnly);
        }
    }

    private static void DrawActionButtons(
        Action previewAction,
        Action replaceAction,
        Action toggleAction,
        bool canReplace)
    {
        EditorGUILayout.Space(5.0f);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Preview / Select Matches"))
            {
                previewAction();
            }

            using (new EditorGUI.DisabledScope(!canReplace))
            {
                if (GUILayout.Button("Replace & Deactivate"))
                {
                    replaceAction();
                }
            }
        }

        if (GUILayout.Button("Toggle Originals / Replacements"))
        {
            toggleAction();
        }
    }

    private bool CanRunSingleReplacement()
    {
        return root != null &&
               !string.IsNullOrWhiteSpace(replacementSearch) &&
               replacementPrefab != null;
    }

    private bool CanRunTreeReplacement()
    {
        return root != null &&
               !string.IsNullOrWhiteSpace(treeSearch) &&
               treePrefabs.Any(prefab => prefab != null);
    }

    private void PreviewMatches(
        string search,
        bool usesRegex,
        bool caseSensitive,
        bool topLevelOnly)
    {
        if (!TryFindOriginalMatches(
                search,
                usesRegex,
                caseSensitive,
                topLevelOnly,
                out List<Transform> matches,
                out string error))
        {
            SetStatus(error, MessageType.Error);
            return;
        }

        Selection.objects = matches
            .Select(match => (UnityEngine.Object)match.gameObject)
            .ToArray();

        SetStatus(
            $"Selected {matches.Count} original object(s) under '{root.name}'.",
            matches.Count > 0 ? MessageType.Info : MessageType.Warning);
    }

    private void ReplaceWithSinglePrefab()
    {
        if (!ValidatePrefab(replacementPrefab, out string prefabError))
        {
            SetStatus(prefabError, MessageType.Error);
            return;
        }

        if (!TryFindOriginalMatches(
                replacementSearch,
                replacementUsesRegex,
                replacementCaseSensitive,
                replacementTopLevelOnly,
                out List<Transform> matches,
                out string searchError))
        {
            SetStatus(searchError, MessageType.Error);
            return;
        }

        if (matches.Count == 0)
        {
            SetStatus("No original objects matched the search.", MessageType.Warning);
            return;
        }

        string groupName = BuildReplacementGroupName(replacementSearch);
        int undoGroup = BeginUndoGroup($"Replace '{replacementSearch}'");

        try
        {
            Transform replacementGroup = GetOrCreateReplacementGroup(groupName);
            ClearReplacementGroup(replacementGroup);

            foreach (Transform source in matches)
            {
                GameObject instance = InstantiatePrefab(
                    replacementPrefab,
                    replacementGroup,
                    source.name);

                ApplyGeneralTransform(
                    instance.transform,
                    source,
                    replacementCopiesRotation,
                    replacementCopiesWorldScale);

                if (replacementCopiesLayer)
                {
                    CopyLayerRecursively(instance, source.gameObject.layer);
                }

                SetActiveWithUndo(source.gameObject, false, "Deactivate original");
            }

            SetActiveWithUndo(replacementGroup.gameObject, true, "Activate replacements");
            Selection.activeTransform = replacementGroup;

            EditorUtility.SetDirty(replacementGroup.gameObject);
            SetStatus(
                $"Created {matches.Count} replacement(s) in '{groupName}'.",
                MessageType.Info);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            SetStatus($"Replacement failed: {exception.Message}", MessageType.Error);
        }
        finally
        {
            Undo.CollapseUndoOperations(undoGroup);
        }
    }

    private void ReplaceTrees()
    {
        List<GameObject> validPrefabs = treePrefabs
            .Where(prefab => prefab != null)
            .ToList();

        foreach (GameObject prefab in validPrefabs)
        {
            if (!ValidatePrefab(prefab, out string prefabError))
            {
                SetStatus(prefabError, MessageType.Error);
                return;
            }
        }

        if (!TryFindOriginalMatches(
                treeSearch,
                treeUsesRegex,
                treeCaseSensitive,
                treeTopLevelOnly,
                out List<Transform> matches,
                out string searchError))
        {
            SetStatus(searchError, MessageType.Error);
            return;
        }

        if (matches.Count == 0)
        {
            SetStatus("No original tree objects matched the search.", MessageType.Warning);
            return;
        }

        NormalizeRange(ref yawRange);
        NormalizeRange(ref uniformScaleRange);

        string groupName = BuildReplacementGroupName(treeSearch);
        int undoGroup = BeginUndoGroup($"Replace trees '{treeSearch}'");

        try
        {
            Transform replacementGroup = GetOrCreateReplacementGroup(groupName);
            ClearReplacementGroup(replacementGroup);

            foreach (Transform source in matches)
            {
                System.Random random = CreateStableRandom(source, randomSeed);
                GameObject prefab = validPrefabs[random.Next(validPrefabs.Count)];

                GameObject instance = InstantiatePrefab(
                    prefab,
                    replacementGroup,
                    source.name);

                ApplyTreeTransform(
                    instance.transform,
                    source,
                    random);

                if (treeCopiesLayer)
                {
                    CopyLayerRecursively(instance, source.gameObject.layer);
                }

                SetActiveWithUndo(source.gameObject, false, "Deactivate original tree");
            }

            SetActiveWithUndo(replacementGroup.gameObject, true, "Activate tree replacements");
            Selection.activeTransform = replacementGroup;

            EditorUtility.SetDirty(replacementGroup.gameObject);
            SetStatus(
                $"Created {matches.Count} deterministic tree replacement(s) in '{groupName}'.",
                MessageType.Info);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            SetStatus($"Tree replacement failed: {exception.Message}", MessageType.Error);
        }
        finally
        {
            Undo.CollapseUndoOperations(undoGroup);
        }
    }

    private void ToggleOriginalsAndReplacements(
        string search,
        bool usesRegex,
        bool caseSensitive,
        bool topLevelOnly)
    {
        if (!TryFindOriginalMatches(
                search,
                usesRegex,
                caseSensitive,
                topLevelOnly,
                out List<Transform> matches,
                out string error))
        {
            SetStatus(error, MessageType.Error);
            return;
        }

        string groupName = BuildReplacementGroupName(search);
        Transform replacementGroup = FindDirectChild(root, groupName);

        if (replacementGroup == null)
        {
            SetStatus(
                $"Replacement group '{groupName}' does not exist yet.",
                MessageType.Warning);
            return;
        }

        bool anyOriginalIsActive = matches.Any(match => match.gameObject.activeSelf);
        bool showOriginals = !anyOriginalIsActive;

        int undoGroup = BeginUndoGroup(
            showOriginals ? "Show original objects" : "Show replacement objects");

        try
        {
            foreach (Transform match in matches)
            {
                SetActiveWithUndo(
                    match.gameObject,
                    showOriginals,
                    "Toggle original object");
            }

            SetActiveWithUndo(
                replacementGroup.gameObject,
                !showOriginals,
                "Toggle replacement group");

            Selection.activeTransform =
                showOriginals
                    ? root
                    : replacementGroup;

            SetStatus(
                showOriginals
                    ? $"Showing {matches.Count} original object(s)."
                    : $"Showing replacement group '{groupName}'.",
                MessageType.Info);
        }
        finally
        {
            Undo.CollapseUndoOperations(undoGroup);
        }
    }

    private bool TryFindOriginalMatches(
        string search,
        bool usesRegex,
        bool caseSensitive,
        bool topLevelOnly,
        out List<Transform> matches,
        out string error)
    {
        matches = new List<Transform>();
        error = string.Empty;

        if (root == null)
        {
            error = "Assign a Root transform first.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(search))
        {
            error = "Enter a non-empty search string.";
            return false;
        }

        Regex regex = null;

        if (usesRegex)
        {
            try
            {
                RegexOptions options = RegexOptions.CultureInvariant;

                if (!caseSensitive)
                {
                    options |= RegexOptions.IgnoreCase;
                }

                regex = new Regex(search, options, RegexTimeout);
            }
            catch (ArgumentException exception)
            {
                error = $"Invalid regular expression: {exception.Message}";
                return false;
            }
        }

        Transform[] hierarchy = root.GetComponentsInChildren<Transform>(true);

        foreach (Transform candidate in hierarchy)
        {
            if (candidate == root || IsInsideReplacementGroup(candidate))
            {
                continue;
            }

            bool isMatch;

            try
            {
                isMatch = usesRegex
                    ? regex.IsMatch(candidate.name)
                    : candidate.name.IndexOf(
                        search,
                        caseSensitive
                            ? StringComparison.Ordinal
                            : StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (RegexMatchTimeoutException)
            {
                error =
                    "The regular expression took too long to evaluate. " +
                    "Use a more specific pattern.";
                matches.Clear();
                return false;
            }

            if (isMatch)
            {
                matches.Add(candidate);
            }
        }

        if (topLevelOnly && matches.Count > 1)
        {
            HashSet<Transform> matchSet = new HashSet<Transform>(matches);

            matches = matches
                .Where(candidate => !HasMatchingAncestor(candidate, matchSet))
                .ToList();
        }

        matches.Sort(
            (left, right) => string.CompareOrdinal(
                GetRelativeHierarchyPath(left),
                GetRelativeHierarchyPath(right)));

        return true;
    }

    private bool IsInsideReplacementGroup(Transform candidate)
    {
        Transform current = candidate;

        while (current != null && current != root)
        {
            if (current.name.EndsWith(
                    ReplacementGroupSuffix,
                    StringComparison.Ordinal))
            {
                return true;
            }

            current = current.parent;
        }

        return false;
    }

    private static bool HasMatchingAncestor(
        Transform candidate,
        HashSet<Transform> matches)
    {
        Transform current = candidate.parent;

        while (current != null)
        {
            if (matches.Contains(current))
            {
                return true;
            }

            current = current.parent;
        }

        return false;
    }

    private Transform GetOrCreateReplacementGroup(string groupName)
    {
        Transform existing = FindDirectChild(root, groupName);

        if (existing != null)
        {
            return existing;
        }

        GameObject groupObject = new GameObject(groupName);
        Undo.RegisterCreatedObjectUndo(groupObject, "Create replacement group");
        Undo.SetTransformParent(groupObject.transform, root, "Parent replacement group");

        groupObject.transform.localPosition = Vector3.zero;
        groupObject.transform.localRotation = Quaternion.identity;
        groupObject.transform.localScale = Vector3.one;

        return groupObject.transform;
    }

    private static Transform FindDirectChild(
        Transform parent,
        string childName)
    {
        if (parent == null)
        {
            return null;
        }

        for (int index = 0; index < parent.childCount; index++)
        {
            Transform child = parent.GetChild(index);

            if (string.Equals(
                    child.name,
                    childName,
                    StringComparison.Ordinal))
            {
                return child;
            }
        }

        return null;
    }

    private static void ClearReplacementGroup(Transform replacementGroup)
    {
        for (int index = replacementGroup.childCount - 1; index >= 0; index--)
        {
            Undo.DestroyObjectImmediate(
                replacementGroup.GetChild(index).gameObject);
        }
    }

    private GameObject InstantiatePrefab(
        GameObject prefab,
        Transform parent,
        string instanceName)
    {
        GameObject instance = PrefabUtility.InstantiatePrefab(
            prefab,
            root.gameObject.scene) as GameObject;

        if (instance == null)
        {
            throw new InvalidOperationException(
                $"Unity could not instantiate prefab '{prefab.name}'.");
        }

        Undo.RegisterCreatedObjectUndo(instance, "Create replacement");
        Vector3 authoredWorldScale = instance.transform.lossyScale;

        Undo.SetTransformParent(
            instance.transform,
            parent,
            "Parent replacement");

        instance.name = instanceName;
        instance.SetActive(true);

        // Preserve the prefab's authored scale after parenting under a potentially scaled root.
        SetWorldScale(instance.transform, authoredWorldScale);

        return instance;
    }

    private static void ApplyGeneralTransform(
        Transform replacement,
        Transform source,
        bool copyRotation,
        bool copyWorldScale)
    {
        Quaternion prefabRotation = replacement.rotation;
        Vector3 prefabWorldScale = replacement.lossyScale;

        replacement.position = source.position;
        replacement.rotation = copyRotation
            ? source.rotation
            : prefabRotation;

        SetWorldScale(
            replacement,
            copyWorldScale
                ? source.lossyScale
                : prefabWorldScale);
    }

    private void ApplyTreeTransform(
        Transform replacement,
        Transform source,
        System.Random random)
    {
        Quaternion prefabRotation = replacement.rotation;
        Vector3 prefabWorldScale = replacement.lossyScale;

        Quaternion rotation = treeCopiesRotation
            ? source.rotation
            : prefabRotation;

        if (randomizeYaw)
        {
            float yaw = NextFloat(random, yawRange.x, yawRange.y);
            rotation = Quaternion.AngleAxis(yaw, Vector3.up) * rotation;
        }

        Vector3 worldScale = treeCopiesWorldScale
            ? source.lossyScale
            : prefabWorldScale;

        if (randomizeUniformScale)
        {
            float multiplier = NextFloat(
                random,
                uniformScaleRange.x,
                uniformScaleRange.y);

            worldScale *= multiplier;
        }

        replacement.SetPositionAndRotation(source.position, rotation);
        SetWorldScale(replacement, worldScale);
    }

    private System.Random CreateStableRandom(
        Transform source,
        int seed)
    {
        string path = GetRelativeHierarchyPath(source);
        int pathHash = StableHash(path);
        int combinedSeed = unchecked((seed * 397) ^ pathHash);

        return new System.Random(combinedSeed);
    }

    private string GetRelativeHierarchyPath(Transform transform)
    {
        Stack<string> parts = new Stack<string>();
        Transform current = transform;

        while (current != null && current != root)
        {
            parts.Push($"{current.name}[{current.GetSiblingIndex()}]");
            current = current.parent;
        }

        return string.Join("/", parts);
    }

    private static int StableHash(string value)
    {
        unchecked
        {
            const uint offsetBasis = 2166136261;
            const uint prime = 16777619;

            uint hash = offsetBasis;

            for (int index = 0; index < value.Length; index++)
            {
                hash ^= value[index];
                hash *= prime;
            }

            return (int)hash;
        }
    }

    private static float NextFloat(
        System.Random random,
        float minimum,
        float maximum)
    {
        return Mathf.Lerp(
            minimum,
            maximum,
            (float)random.NextDouble());
    }

    private static void NormalizeRange(ref Vector2 range)
    {
        if (range.x > range.y)
        {
            float temporary = range.x;
            range.x = range.y;
            range.y = temporary;
        }
    }

    private static void SetWorldScale(
        Transform transform,
        Vector3 desiredWorldScale)
    {
        Transform parent = transform.parent;

        if (parent == null)
        {
            transform.localScale = desiredWorldScale;
            return;
        }

        Vector3 parentScale = parent.lossyScale;

        transform.localScale = new Vector3(
            SafeDivide(desiredWorldScale.x, parentScale.x),
            SafeDivide(desiredWorldScale.y, parentScale.y),
            SafeDivide(desiredWorldScale.z, parentScale.z));
    }

    private static float SafeDivide(float numerator, float denominator)
    {
        return Mathf.Abs(denominator) > 0.000001f
            ? numerator / denominator
            : numerator;
    }

    private static void CopyLayerRecursively(
        GameObject target,
        int layer)
    {
        target.layer = layer;

        foreach (Transform child in target.transform)
        {
            CopyLayerRecursively(child.gameObject, layer);
        }
    }

    private static void SetActiveWithUndo(
        GameObject target,
        bool active,
        string undoName)
    {
        if (target.activeSelf == active)
        {
            return;
        }

        Undo.RecordObject(target, undoName);
        target.SetActive(active);
        EditorUtility.SetDirty(target);
    }

    private static bool ValidatePrefab(
        GameObject prefab,
        out string error)
    {
        if (prefab == null)
        {
            error = "Assign a candidate prefab.";
            return false;
        }

        if (!PrefabUtility.IsPartOfPrefabAsset(prefab))
        {
            error =
                $"'{prefab.name}' is not a Prefab asset. " +
                "Drag a prefab from the Project window, not a scene object.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static int BeginUndoGroup(string name)
    {
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(name);
        return group;
    }

    private static string BuildReplacementGroupName(string search)
    {
        StringBuilder builder = new StringBuilder(search.Length);
        bool previousWasSeparator = false;

        foreach (char character in search)
        {
            bool valid =
                char.IsLetterOrDigit(character) ||
                character == '_' ||
                character == '-';

            if (valid)
            {
                builder.Append(character);
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator)
            {
                builder.Append('_');
                previousWasSeparator = true;
            }
        }

        string key = builder
            .ToString()
            .Trim('_');

        if (string.IsNullOrWhiteSpace(key))
        {
            key = "replacement";
        }

        const int maximumKeyLength = 64;

        if (key.Length > maximumKeyLength)
        {
            key = key.Substring(0, maximumKeyLength);
        }

        return key + ReplacementGroupSuffix;
    }

    private void SetStatus(
        string message,
        MessageType type)
    {
        statusMessage = message;
        statusType = type;
        Repaint();
    }
}
#endif
