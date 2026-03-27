using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using US13.ChemistryComponents;
using US13.Items.Food;

/// <summary>
/// Scans all Item-derived prefabs for serialized references to ReagentContainer or Edible
/// that are null despite the prefab having that component available.
/// Catches both "Missing" refs and refs nulled out by prior cleanup tools.
/// </summary>
public static class FindBrokenReagentRefs
{
	const string ItemPrefabPath = "Assets/Prefabs/Items/Item.prefab";

	[MenuItem("Tools/Find Broken Reagent Refs (Dry Run)")]
	static void DryRun() => Run(true);

	[MenuItem("Tools/Find Broken Reagent Refs (Fix)")]
	static void Fix() => Run(false);

	static void Run(bool dryRun)
	{
		var itemPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ItemPrefabPath);
		if (itemPrefab == null)
		{
			Debug.LogError($"Could not load Item prefab at {ItemPrefabPath}");
			return;
		}

		int prefabCount = 0;
		int brokenRefCount = 0;
		var report = new StringBuilder();
		report.AppendLine($"=== {(dryRun ? "DRY RUN" : "FIXING")} Broken Reagent/Edible Refs ===");

		var guids = AssetDatabase.FindAssets("t:Prefab");
		for (int g = 0; g < guids.Length; g++)
		{
			string path = AssetDatabase.GUIDToAssetPath(guids[g]);
			var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
			if (go == null) continue;

			if (IsItemDescendant(go, itemPrefab) == false) continue;

			if (g % 200 == 0)
				EditorUtility.DisplayProgressBar("Scanning prefabs", path, (float)g / guids.Length);

			bool hasRC = go.GetComponent<ReagentContainer>() != null;
			bool hasEdible = go.GetComponent<Edible>() != null;
			if (hasRC == false && hasEdible == false) continue;

			var brokenFields = FindBrokenRefs(go, hasRC, hasEdible);
			if (brokenFields.Count == 0) continue;

			prefabCount++;
			brokenRefCount += brokenFields.Count;
			report.AppendLine($"\n[{path}]");
			foreach (var entry in brokenFields)
				report.AppendLine($"  {entry.componentType}.{entry.propertyPath} -> null {entry.refTypeName} (iid={entry.instanceId})");

			if (dryRun == false)
			{
				FixBrokenRefs(path, hasRC, hasEdible);
			}
		}

		EditorUtility.ClearProgressBar();

		report.AppendLine($"\n=== Done: {prefabCount} prefabs with {brokenRefCount} broken refs ===");
		Debug.Log(report);

		if (dryRun == false)
		{
			AssetDatabase.SaveAssets();
			AssetDatabase.Refresh();
		}
	}

	struct BrokenRef
	{
		public string componentType;
		public string propertyPath;
		public string refTypeName;
		public int instanceId;
	}

	static List<BrokenRef> FindBrokenRefs(GameObject go, bool hasRC, bool hasEdible)
	{
		var results = new List<BrokenRef>();
		var components = go.GetComponents<Component>();

		foreach (var comp in components)
		{
			if (comp == null) continue;

			var so = new SerializedObject(comp);
			var prop = so.GetIterator();

			while (prop.Next(true))
			{
				if (prop.propertyType != SerializedPropertyType.ObjectReference) continue;
				if (prop.objectReferenceValue != null) continue;

				var fieldType = GetFieldType(comp, prop.propertyPath);
				if (fieldType == null) continue;

				bool isRCField = hasRC && typeof(ReagentContainer).IsAssignableFrom(fieldType);
				bool isEdibleField = hasEdible && typeof(Edible).IsAssignableFrom(fieldType);
				if (isRCField == false && isEdibleField == false) continue;

				// Skip self-references (e.g. ReagentContainer has fields typed as ReagentContainer
				// that are intentionally null for "no transfer" behavior)
				if (comp is ReagentContainer && isRCField) continue;
				if (comp is Edible && isEdibleField) continue;

				results.Add(new BrokenRef
				{
					componentType = comp.GetType().Name,
					propertyPath = prop.propertyPath,
					refTypeName = fieldType.Name,
					instanceId = prop.objectReferenceInstanceIDValue
				});
			}
		}

		return results;
	}

	static void FixBrokenRefs(string path, bool hasRC, bool hasEdible)
	{
		using var scope = new PrefabUtility.EditPrefabContentsScope(path);
		var root = scope.prefabContentsRoot;

		var rc = hasRC ? root.GetComponent<ReagentContainer>() : null;
		var edible = hasEdible ? root.GetComponent<Edible>() : null;

		var components = root.GetComponents<Component>();
		foreach (var comp in components)
		{
			if (comp == null) continue;

			var so = new SerializedObject(comp);
			var prop = so.GetIterator();
			bool modified = false;

			while (prop.Next(true))
			{
				if (prop.propertyType != SerializedPropertyType.ObjectReference) continue;
				if (prop.objectReferenceValue != null) continue;

				var fieldType = GetFieldType(comp, prop.propertyPath);
				if (fieldType == null) continue;

				bool isRCField = rc != null && typeof(ReagentContainer).IsAssignableFrom(fieldType);
				bool isEdibleField = edible != null && typeof(Edible).IsAssignableFrom(fieldType);
				if (isRCField == false && isEdibleField == false) continue;

				if (comp is ReagentContainer && isRCField) continue;
				if (comp is Edible && isEdibleField) continue;

				prop.objectReferenceValue = isRCField ? (Object)rc : edible;
				modified = true;
			}

			if (modified) so.ApplyModifiedPropertiesWithoutUndo();
		}
	}

	static System.Type GetFieldType(Component comp, string propertyPath)
	{
		string rootField = propertyPath;
		int dotIdx = rootField.IndexOf('.');
		if (dotIdx >= 0)
			rootField = rootField.Substring(0, dotIdx);

		var type = comp.GetType();
		while (type != null)
		{
			var field = type.GetField(rootField,
				System.Reflection.BindingFlags.Instance |
				System.Reflection.BindingFlags.Public |
				System.Reflection.BindingFlags.NonPublic);

			if (field != null)
			{
				var ft = field.FieldType;
				if (ft.IsArray)
					return ft.GetElementType();
				if (ft.IsGenericType && ft.GetGenericTypeDefinition() == typeof(List<>))
					return ft.GetGenericArguments()[0];
				return ft;
			}

			type = type.BaseType;
		}

		return null;
	}

	static bool IsItemDescendant(GameObject prefab, GameObject itemPrefab)
	{
		var current = prefab;
		int safety = 50;
		while (current != null && safety-- > 0)
		{
			if (current == itemPrefab) return true;

			var source = PrefabUtility.GetCorrespondingObjectFromSource(current);
			if (source == null || source == current) break;
			current = source;
		}

		return false;
	}
}
