using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using US13.ChemistryComponents;
using US13.Items.Food;

/// <summary>
/// Consolidates duplicate ReagentContainer and Edible components on prefab variants.
///
/// Strategy: Unity APIs for analysis, direct YAML file I/O for persistence.
/// This bypasses PrefabUtility.SetPropertyModifications which silently fails to persist
/// in many cases (especially inherited duplicates with no EditPrefabContentsScope).
///
/// Two cases:
/// 1. addedHere=true: Duplicate was added at this variant level.
///    → Collect values via SerializedObject, destroy in EditPrefabContentsScope,
///      then insert PropertyModification YAML entries directly into the saved file.
/// 2. addedHere=false: Duplicate is inherited from a parent variant.
///    → Global find-replace the duplicate's {fileID,guid} with the base's {fileID,guid}
///      in the .prefab YAML. This retargets all modifications and objectReferences.
///
/// Processing order: deepest variants first, so leaf overrides are retargeted before
/// the parent that added the duplicate destroys it.
/// </summary>
public static class CleanDuplicateComponents
{
	const string ItemPrefabPath = "Assets/Prefabs/Items/Item.prefab";

	static readonly HashSet<string> SkipProps = new HashSet<string>
	{
		"m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance",
		"m_PrefabAsset", "m_GameObject", "m_Script", "m_EditorHideFlags",
		"m_Name", "m_EditorClassIdentifier"
	};

	struct Analysis
	{
		public bool needed;
		public bool addedHere;
		public string dupRef;          // "fileID: X, guid: Y" — line-safe search pattern (no braces/type)
		public string baseRef;         // "fileID: A, guid: B" — replacement pattern
		public List<string> modsYaml;  // YAML modification entries to insert (addedHere only)
	}

	[MenuItem("Tools/Clean Duplicate Components (Dry Run)")]
	static void DryRun() => Run(true);

	[MenuItem("Tools/Clean Duplicate Components")]
	static void Execute() => Run(false);

	static void Run(bool dryRun)
	{
		var candidates = new List<(string path, int depth)>();

		foreach (var guid in AssetDatabase.FindAssets("t:Prefab"))
		{
			string path = AssetDatabase.GUIDToAssetPath(guid);
			var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
			if (go == null || PrefabUtility.GetPrefabAssetType(go) != PrefabAssetType.Variant)
				continue;

			var rcs = go.GetComponents<ReagentContainer>();
			var edibles = go.GetComponents<Edible>();
			if (rcs.Length <= 1 && edibles.Length <= 1) continue;

			candidates.Add((path, GetVariantDepth(go)));
		}

		candidates.Sort((a, b) => b.depth.CompareTo(a.depth));

		int fixCount = 0, skipCount = 0;
		Debug.Log($"=== {(dryRun ? "DRY RUN" : "EXECUTING")} === {candidates.Count} candidates");

		foreach (var (path, depth) in candidates)
		{
			var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
			if (go == null) continue;

			if (go.GetComponents<ReagentContainer>().Length <= 1 &&
			    go.GetComponents<Edible>().Length <= 1)
			{
				continue; // No longer has duplicates (parent was already processed)
			}

			var log = new StringBuilder();
			log.AppendLine($"[{path}] depth={depth}");

			var rc = AnalyzeComponent<ReagentContainer>(go, log);
			var ed = AnalyzeComponent<Edible>(go, log);

			if (rc.needed == false && ed.needed == false)
			{
				skipCount++;
				Debug.Log(log);
				continue;
			}

			if (dryRun)
			{
				fixCount++;
				Debug.Log(log);
				continue;
			}

			// === PHASE 1: Scope — destroy addedHere duplicates ===
			bool anyAdded = (rc.needed && rc.addedHere) || (ed.needed && ed.addedHere);
			if (anyAdded)
			{
				using (var scope = new PrefabUtility.EditPrefabContentsScope(path))
				{
					var root = scope.prefabContentsRoot;
					if (rc.needed && rc.addedHere)
						DestroyDuplicatesInScope<ReagentContainer>(root, log);
					if (ed.needed && ed.addedHere)
						DestroyDuplicatesInScope<Edible>(root, log);
				}
				// Scope saved structural changes to disk
			}

			// === PHASE 2: Direct YAML text manipulation ===
			string fullPath = Path.GetFullPath(path);
			string yaml = File.ReadAllText(fullPath);
			bool changed = false;

			// Retarget inherited duplicates (addedHere=false)
			if (rc.needed && rc.addedHere == false && rc.dupRef != null)
			{
				string before = yaml;
				yaml = yaml.Replace(rc.dupRef, rc.baseRef);
				if (yaml != before)
				{
					int count = CountOccurrences(before, rc.dupRef);
					log.AppendLine($"  Retargeted {count} RC refs: {rc.dupRef} -> {rc.baseRef}");
					changed = true;
				}
			}

			if (ed.needed && ed.addedHere == false && ed.dupRef != null)
			{
				string before = yaml;
				yaml = yaml.Replace(ed.dupRef, ed.baseRef);
				if (yaml != before)
				{
					int count = CountOccurrences(before, ed.dupRef);
					log.AppendLine($"  Retargeted {count} Edible refs: {ed.dupRef} -> {ed.baseRef}");
					changed = true;
				}
			}

			// Insert collected modifications (addedHere=true)
			if (rc.needed && rc.addedHere && rc.modsYaml?.Count > 0)
			{
				yaml = InsertModifications(yaml, rc.modsYaml);
				log.AppendLine($"  Inserted {rc.modsYaml.Count} RC modification entries");
				changed = true;
			}

			if (ed.needed && ed.addedHere && ed.modsYaml?.Count > 0)
			{
				yaml = InsertModifications(yaml, ed.modsYaml);
				log.AppendLine($"  Inserted {ed.modsYaml.Count} Edible modification entries");
				changed = true;
			}

			if (changed)
			{
				File.WriteAllText(fullPath, yaml);
				AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
				fixCount++;
				log.AppendLine("  Written to disk");
			}
			else
			{
				skipCount++;
				// Not a warning — inherited variants with no refs to the dup in their file are expected
			}

			Debug.Log(log);
		}

		if (dryRun == false)
		{
			AssetDatabase.SaveAssets();
			AssetDatabase.Refresh();
		}

		Debug.Log($"=== Done: {fixCount} fixed, {skipCount} skipped ===");
	}

	// ─── Analysis (Unity APIs only — no persistence) ───

	/// <summary>
	/// Gets the GUID of the variant's immediate parent (source) prefab.
	/// In Unity YAML, PropertyModification target fields always reference
	/// components using {fileID: ORIGINAL_FILE_ID, guid: IMMEDIATE_PARENT_GUID}.
	/// </summary>
	static string GetParentGuid(GameObject variant)
	{
		var source = PrefabUtility.GetCorrespondingObjectFromSource(variant);
		if (source == null) return null;
		return AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(source));
	}

	static Analysis AnalyzeComponent<T>(GameObject prefab, StringBuilder log) where T : Component
	{
		var comps = prefab.GetComponents<T>();
		if (comps.Length <= 1) return default;

		T baseComp = null, dup = null;
		foreach (var c in comps)
		{
			if (IsFromItemPrefab(c) && baseComp == null)
				baseComp = c;
			else if (dup == null)
				dup = c;
			else if (c is Behaviour b && b.enabled)
				dup = c; // Prefer enabled duplicate
		}

		if (baseComp == null || dup == null)
		{
			log.AppendLine($"  {typeof(T).Name}: could not identify base/duplicate, skipping");
			return default;
		}

		var dupSource = PrefabUtility.GetCorrespondingObjectFromSource(dup);
		var baseSource = PrefabUtility.GetCorrespondingObjectFromSource(baseComp);

		if (baseSource == null)
		{
			log.AppendLine($"  {typeof(T).Name}: base has no source in parent, skipping");
			return default;
		}

		string parentGuid = GetParentGuid(prefab);

		// --- addedHere=true: duplicate was added at this variant level ---
		if (dupSource == null)
		{
			var mods = CollectModificationsAsYaml(dup, baseSource, parentGuid);
			log.AppendLine($"  {typeof(T).Name}: ADDED HERE, collected {mods.Count} modification entries");
			return new Analysis { needed = true, addedHere = true, modsYaml = mods };
		}

		// --- addedHere=false: inherited duplicate — check for overrides at this level ---
		var allMods = PrefabUtility.GetPropertyModifications(prefab);
		bool hasOverrides = allMods != null &&
		                    allMods.Any(m => m.target == dupSource && m.propertyPath != "m_Script");

		if (hasOverrides == false)
		{
			log.AppendLine($"  {typeof(T).Name}: inherited, no overrides at this level (ancestor handles it)");
			return default;
		}

		string dRef = BuildTargetRef(dupSource, parentGuid);
		string bRef = BuildTargetRef(baseSource, parentGuid);
		log.AppendLine($"  {typeof(T).Name}: INHERITED with overrides, will retarget {dRef} -> {bRef}");

		return new Analysis { needed = true, addedHere = false, dupRef = dRef, baseRef = bRef };
	}

	// ─── Collect values from duplicate as YAML modification entries ───

	static List<string> CollectModificationsAsYaml(Component source, Object baseSource, string parentGuid)
	{
		var result = new List<string>();
		string targetRef = FullYamlRef(baseSource, parentGuid);
		var so = new SerializedObject(source);
		var prop = so.GetIterator();

		while (prop.Next(true))
		{
			if (SkipProps.Contains(prop.propertyPath)) continue;
			if (prop.hasChildren) continue;

			string value, objRef;
			if (prop.propertyType == SerializedPropertyType.ObjectReference)
			{
				value = "";
				objRef = FullYamlRef(prop.objectReferenceValue, parentGuid);
			}
			else
			{
				value = SerializedPropToString(prop);
				objRef = "{fileID: 0}";
			}

			result.Add(FormatModEntry(targetRef, prop.propertyPath, value, objRef));
		}

		return result;
	}

	static string FormatModEntry(string target, string propertyPath, string value, string objectReference)
	{
		// Unity quotes property paths containing special characters like [ ]
		string quotedPath = propertyPath.Contains("[")
			? $"'{propertyPath}'"
			: propertyPath;

		return $"    - target: {target}\n" +
		       $"      propertyPath: {quotedPath}\n" +
		       $"      value: {value}\n" +
		       $"      objectReference: {objectReference}";
	}

	// ─── YAML reference serialization ───

	/// <summary>
	/// Returns "fileID: X, guid: Y" — the line-safe substring that appears in YAML
	/// regardless of whether Unity wraps the line after the guid.
	/// Unity wraps long refs across lines: {fileID: X, guid: Y,\n        type: 3}
	/// so we can't search for the full "{fileID: X, guid: Y, type: 3}" string.
	/// Used for retarget search/replace.
	/// </summary>
	static string BuildTargetRef(Object sourceObj, string parentGuid)
	{
		if (sourceObj == null || parentGuid == null) return null;
		if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sourceObj, out string _, out long fileId) == false)
			return null;
		return $"fileID: {fileId}, guid: {parentGuid}";
	}

	/// <summary>
	/// Builds a full YAML reference with Unity-style line wrapping for insertion.
	/// Long refs wrap: {fileID: X, guid: Y,\n        type: T}
	/// Short refs stay inline: {fileID: 0}
	/// </summary>
	static string FullYamlRef(Object obj, string parentGuid)
	{
		if (obj == null) return "{fileID: 0}";
		if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string guid, out long localId) == false)
			return "{fileID: 0}";

		if (obj is Component || obj is GameObject)
			return WrapRef(localId, parentGuid, 3);

		return WrapRef(localId, guid, 2);
	}

	static string WrapRef(long fileId, string guid, int type)
	{
		// Match Unity's line-wrapping: break after guid comma, indent continuation with 8 spaces
		return $"{{fileID: {fileId}, guid: {guid},\n        type: {type}}}";
	}

	// ─── SerializedProperty value to string ───

	static string SerializedPropToString(SerializedProperty prop)
	{
		switch (prop.propertyType)
		{
			case SerializedPropertyType.Integer:
				return prop.type == "long"
					? prop.longValue.ToString()
					: prop.intValue.ToString();
			case SerializedPropertyType.Boolean:
				return prop.boolValue ? "1" : "0";
			case SerializedPropertyType.Float:
				return prop.type == "double"
					? prop.doubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture)
					: prop.floatValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
			case SerializedPropertyType.String:
				return prop.stringValue;
			case SerializedPropertyType.Enum:
			case SerializedPropertyType.LayerMask:
			case SerializedPropertyType.Character:
			case SerializedPropertyType.ArraySize:
				return prop.intValue.ToString();
			default:
				return prop.intValue.ToString();
		}
	}

	// ─── YAML text manipulation ───

	/// <summary>
	/// Inserts modification entries into the m_Modifications YAML array.
	/// Handles both non-empty arrays (append) and empty arrays (convert from []).
	/// </summary>
	static string InsertModifications(string yaml, List<string> entries)
	{
		string nl = yaml.Contains("\r\n") ? "\r\n" : "\n";
		var lines = yaml.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None);
		var result = new List<string>(lines.Length + entries.Count * 4);
		bool inMods = false;
		bool inserted = false;

		for (int i = 0; i < lines.Length; i++)
		{
			string trimmed = lines[i].TrimEnd();

			// Handle empty modifications array: m_Modifications: []
			if (inserted == false && trimmed == "    m_Modifications: []")
			{
				result.Add("    m_Modifications:");
				foreach (var entry in entries)
					foreach (var entryLine in entry.Split('\n'))
						result.Add(entryLine);
				inserted = true;
				continue;
			}

			// Detect start of non-empty modifications array
			if (inserted == false && trimmed == "    m_Modifications:")
			{
				inMods = true;
				result.Add(lines[i]);
				continue;
			}

			// Detect end of modifications array — insert before next section
			if (inMods && inserted == false)
			{
				bool isModContent = trimmed.StartsWith("    - ") ||
				                    trimmed.StartsWith("      ") ||
				                    string.IsNullOrWhiteSpace(trimmed);
				if (isModContent == false)
				{
					foreach (var entry in entries)
						foreach (var entryLine in entry.Split('\n'))
							result.Add(entryLine);
					inserted = true;
					inMods = false;
				}
			}

			result.Add(lines[i]);
		}

		// If modifications section was at end of file
		if (inMods && inserted == false)
		{
			foreach (var entry in entries)
				foreach (var entryLine in entry.Split('\n'))
					result.Add(entryLine);
		}

		return string.Join(nl, result);
	}

	// ─── Scope-based duplicate destruction ───

	static void DestroyDuplicatesInScope<T>(GameObject root, StringBuilder log) where T : Component
	{
		var comps = root.GetComponents<T>();
		if (comps.Length <= 1) return;

		// Inside EditPrefabContentsScope, identify base as the disabled component
		// (Item.prefab has both RC and Edible disabled), duplicate as the enabled one.
		T baseComp = null;
		var dups = new List<T>();

		foreach (var c in comps)
		{
			bool isEnabled = c is Behaviour b && b.enabled;
			if (isEnabled == false && baseComp == null)
				baseComp = c;
			else
				dups.Add(c);
		}

		// Fallback: if no disabled component, treat first as base
		if (baseComp == null)
		{
			baseComp = comps[0];
			dups = comps.Skip(1).ToList();
		}

		// Retarget references from duplicates to base on sibling components
		var allComponents = root.GetComponentsInChildren<Component>(true);
		foreach (var dup in dups)
		{
			foreach (var other in allComponents)
			{
				if (other == null || other == dup || other == baseComp) continue;

				var so = new SerializedObject(other);
				var prop = so.GetIterator();
				bool modified = false;

				while (prop.Next(true))
				{
					if (prop.propertyType == SerializedPropertyType.ObjectReference &&
					    prop.objectReferenceValue == dup)
					{
						prop.objectReferenceValue = baseComp;
						modified = true;
					}
				}

				if (modified) so.ApplyModifiedPropertiesWithoutUndo();
			}

			log.AppendLine($"  Destroyed {typeof(T).Name} duplicate in scope");
			Object.DestroyImmediate(dup, true);
		}
	}

	// ─── Helpers ───

	static bool IsFromItemPrefab(Component c)
	{
		var orig = PrefabUtility.GetCorrespondingObjectFromOriginalSource(c);
		return orig != null && AssetDatabase.GetAssetPath(orig) == ItemPrefabPath;
	}

	static int GetVariantDepth(GameObject prefab)
	{
		int depth = 0;
		var current = prefab;
		while (current != null && PrefabUtility.GetPrefabAssetType(current) == PrefabAssetType.Variant)
		{
			depth++;
			var src = PrefabUtility.GetCorrespondingObjectFromSource(current);
			if (src == null || src == current) break;
			current = src;
		}
		return depth;
	}

	static int CountOccurrences(string text, string pattern)
	{
		int count = 0, idx = 0;
		while ((idx = text.IndexOf(pattern, idx, System.StringComparison.Ordinal)) >= 0)
		{
			count++;
			idx += pattern.Length;
		}
		return count;
	}
}
