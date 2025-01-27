﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GLTF.Schema;
using UnityEngine;
using UnityEngine.Rendering;
using UnityGLTF.Extensions;

#if UNITY_EDITOR // required for in-editor access to non-readable meshes
using UnityEditor;
#endif

namespace UnityGLTF
{
	public partial class GLTFSceneExporter
	{
		private struct MeshAccessors
		{
			public AccessorId aPosition, aNormal, aTangent, aTexcoord0, aTexcoord1, aColor0;
			public Dictionary<int, MeshPrimitive> subMeshPrimitives;
		}

		private struct BlendShapeAccessors
		{
			public List<Dictionary<string, AccessorId>> targets;
			public List<Double> weights;
			public List<string> targetNames;
		}

		private readonly Dictionary<Mesh, MeshAccessors> _meshToPrims = new Dictionary<Mesh, MeshAccessors>();
		private readonly Dictionary<Mesh, BlendShapeAccessors> _meshToBlendShapeAccessors = new Dictionary<Mesh, BlendShapeAccessors>();

		public void RegisterPrimitivesWithNode(Node node, List<UniquePrimitive> uniquePrimitives)
		{
			// associate unity meshes with gltf mesh id
			foreach (var primKey in uniquePrimitives)
			{
				_primOwner[primKey] = node.Mesh;
			}
		}

		private static List<UniquePrimitive> GetUniquePrimitivesFromGameObjects(IEnumerable<GameObject> primitives)
		{
			var primKeys = new List<UniquePrimitive>();

			foreach (var prim in primitives)
			{
				Mesh meshObj = null;
				SkinnedMeshRenderer smr = null;
				var filter = prim.GetComponent<MeshFilter>();
				if (filter)
				{
					meshObj = filter.sharedMesh;
				}
				else
				{
					smr = prim.GetComponent<SkinnedMeshRenderer>();
					if (smr)
					{
						meshObj = smr.sharedMesh;
					}
				}

				if (!meshObj)
				{
					Debug.LogWarning($"MeshFilter.sharedMesh on GameObject:{prim.name} is missing, skipping", prim);
					exportPrimitiveMarker.End();
					return null;
				}


#if UNITY_EDITOR
				if (!MeshIsReadable(meshObj) && EditorUtility.IsPersistent(meshObj))
				{
#if UNITY_2019_3_OR_NEWER
					var assetPath = AssetDatabase.GetAssetPath(meshObj);
					if(assetPath?.Length > 30) assetPath = "..." + assetPath.Substring(assetPath.Length - 30);
					if(EditorUtility.DisplayDialog("Exporting mesh but mesh is not readable",
							$"The mesh {meshObj.name} is not readable. Do you want to change its import settings and make it readable now?\n\n" + assetPath,
							"Make it readable", "No, skip mesh",
							DialogOptOutDecisionType.ForThisSession, MakeMeshReadableDialogueDecisionKey))
#endif
					{
						var path = AssetDatabase.GetAssetPath(meshObj);
						var importer = AssetImporter.GetAtPath(path) as ModelImporter;
						if (importer)
						{
							importer.isReadable = true;
							importer.SaveAndReimport();
						}
					}
#if UNITY_2019_3_OR_NEWER
					else
					{
						Debug.LogWarning($"The mesh {meshObj.name} is not readable. Skipping", null);
						exportPrimitiveMarker.End();
						return null;
					}
#endif
				}
#endif

				if (Application.isPlaying && !MeshIsReadable(meshObj))
				{
					Debug.LogWarning($"The mesh {meshObj.name} is not readable. Skipping", null);
					exportPrimitiveMarker.End();
					return null;
				}

				var renderer = prim.GetComponent<MeshRenderer>();
				if (!renderer) smr = prim.GetComponent<SkinnedMeshRenderer>();

				if(!renderer && !smr)
				{
					Debug.LogWarning("GameObject does have neither renderer nor SkinnedMeshRenderer! " + prim.name, prim);
					exportPrimitiveMarker.End();
					return null;
				}

				var materialsObj = renderer ? renderer.sharedMaterials : smr.sharedMaterials;

				var primKey = new UniquePrimitive();
				primKey.Mesh = meshObj;
				primKey.Materials = materialsObj;
				primKey.SkinnedMeshRenderer = smr;

				primKeys.Add(primKey);
			}

			return primKeys;
		}

		/// <summary>
		/// Convenience wrapper around ExportMesh(string, List<UniquePrimitive>)
		/// </summary>
		public MeshId ExportMesh(Mesh mesh)
		{
			var uniquePrimitives = new List<UniquePrimitive>
			{
				new UniquePrimitive()
				{
					Mesh = mesh,
					SkinnedMeshRenderer = null,
					Materials = new [] { default(Material) },
				}
			};
			return ExportMesh(mesh.name, uniquePrimitives);
		}

		public MeshId ExportMesh(string name, List<UniquePrimitive> uniquePrimitives)
		{
			exportMeshMarker.Begin();

			// check if this set of primitives is already a mesh
			MeshId existingMeshId = null;

			foreach (var prim in uniquePrimitives)
			{
				MeshId tempMeshId;
				if (_primOwner.TryGetValue(prim, out tempMeshId) && (existingMeshId == null || tempMeshId == existingMeshId))
				{
					existingMeshId = tempMeshId;
				}
				else
				{
					existingMeshId = null;
					break;
				}
			}

			// if so, return that mesh id
			if (existingMeshId != null)
			{
				return existingMeshId;
			}

			// if not, create new mesh and return its id
			var mesh = new GLTFMesh();

			if (settings.ExportNames)
			{
				mesh.Name = name;
			}

			mesh.Primitives = new List<MeshPrimitive>(uniquePrimitives.Count);
			foreach (var primKey in uniquePrimitives)
			{
				MeshPrimitive[] meshPrimitives = ExportPrimitive(primKey, mesh);
				if (meshPrimitives != null)
				{
					mesh.Primitives.AddRange(meshPrimitives);
				}
			}

			var id = new MeshId
			{
				Id = _root.Meshes.Count,
				Root = _root
			};

			exportMeshMarker.End();

			if (mesh.Primitives.Count > 0)
			{
				_root.Meshes.Add(mesh);
				return id;
			}

			return null;
		}

		// a mesh *might* decode to multiple prims if there are submeshes
		private MeshPrimitive[] ExportPrimitive(UniquePrimitive primKey, GLTFMesh mesh)
		{
			exportPrimitiveMarker.Begin();

			Mesh meshObj = primKey.Mesh;
			Material[] materialsObj = primKey.Materials;

			var prims = new MeshPrimitive[meshObj.subMeshCount];
			List<MeshPrimitive> nonEmptyPrims = null;
			var vertices = meshObj.vertices;
			if (vertices.Length < 1)
			{
				Debug.LogWarning("MeshFilter does not contain any vertices, won't export: " + meshObj.name, meshObj);
				exportPrimitiveMarker.End();
				return null;
			}

			if (!_meshToPrims.ContainsKey(meshObj))
			{
				AccessorId aPosition = null, aNormal = null, aTangent = null, aTexcoord0 = null, aTexcoord1 = null, aColor0 = null;

				aPosition = ExportAccessor(SchemaExtensions.ConvertVector3CoordinateSpaceAndCopy(meshObj.vertices, SchemaExtensions.CoordinateSpaceConversionScale));

				if (meshObj.normals.Length != 0)
					aNormal = ExportAccessor(SchemaExtensions.ConvertVector3CoordinateSpaceAndCopy(meshObj.normals, SchemaExtensions.CoordinateSpaceConversionScale));

				if (meshObj.tangents.Length != 0)
					aTangent = ExportAccessor(SchemaExtensions.ConvertVector4CoordinateSpaceAndCopy(meshObj.tangents, SchemaExtensions.TangentSpaceConversionScale));

				if (meshObj.uv.Length != 0)
					aTexcoord0 = ExportAccessor(SchemaExtensions.FlipTexCoordArrayVAndCopy(meshObj.uv));

				if (meshObj.uv2.Length != 0)
					aTexcoord1 = ExportAccessor(SchemaExtensions.FlipTexCoordArrayVAndCopy(meshObj.uv2));

				if (settings.ExportVertexColors && meshObj.colors.Length != 0)
					aColor0 = ExportAccessor(QualitySettings.activeColorSpace == ColorSpace.Linear ? meshObj.colors : meshObj.colors.ToLinear(), true);

				aPosition.Value.BufferView.Value.Target = BufferViewTarget.ArrayBuffer;
				if (aNormal != null) aNormal.Value.BufferView.Value.Target = BufferViewTarget.ArrayBuffer;
				if (aTangent != null) aTangent.Value.BufferView.Value.Target = BufferViewTarget.ArrayBuffer;
				if (aTexcoord0 != null) aTexcoord0.Value.BufferView.Value.Target = BufferViewTarget.ArrayBuffer;
				if (aTexcoord1 != null) aTexcoord1.Value.BufferView.Value.Target = BufferViewTarget.ArrayBuffer;
				if (aColor0 != null) aColor0.Value.BufferView.Value.Target = BufferViewTarget.ArrayBuffer;

				_meshToPrims.Add(meshObj, new MeshAccessors()
				{
					aPosition = aPosition,
					aNormal = aNormal,
					aTangent = aTangent,
					aTexcoord0 = aTexcoord0,
					aTexcoord1 = aTexcoord1,
					aColor0 = aColor0,
					subMeshPrimitives = new Dictionary<int, MeshPrimitive>()
				});
			}

			var accessors = _meshToPrims[meshObj];

			// walk submeshes and export the ones with non-null meshes
			for (int submesh = 0; submesh < meshObj.subMeshCount; submesh++)
			{
				if (submesh >= materialsObj.Length) continue;
				if (!materialsObj[submesh]) continue;

				if (!accessors.subMeshPrimitives.ContainsKey(submesh))
				{
					var primitive = new MeshPrimitive();

					var topology = meshObj.GetTopology(submesh);
					var indices = meshObj.GetIndices(submesh);
					if (topology == MeshTopology.Triangles) SchemaExtensions.FlipTriangleFaces(indices);

					primitive.Mode = GetDrawMode(topology);
					primitive.Indices = ExportAccessor(indices, true);
					primitive.Indices.Value.BufferView.Value.Target = BufferViewTarget.ElementArrayBuffer;

					primitive.Attributes = new Dictionary<string, AccessorId>();
					primitive.Attributes.Add(SemanticProperties.POSITION, accessors.aPosition);

					if (accessors.aNormal != null)
						primitive.Attributes.Add(SemanticProperties.NORMAL, accessors.aNormal);
					if (accessors.aTangent != null)
						primitive.Attributes.Add(SemanticProperties.TANGENT, accessors.aTangent);
					if (accessors.aTexcoord0 != null)
						primitive.Attributes.Add(SemanticProperties.TEXCOORD_0, accessors.aTexcoord0);
					if (accessors.aTexcoord1 != null)
						primitive.Attributes.Add(SemanticProperties.TEXCOORD_1, accessors.aTexcoord1);
					if (accessors.aColor0 != null)
						primitive.Attributes.Add(SemanticProperties.COLOR_0, accessors.aColor0);

					primitive.Material = null;

					ExportBlendShapes(primKey.SkinnedMeshRenderer, meshObj, submesh, primitive, mesh);

					accessors.subMeshPrimitives.Add(submesh, primitive);
				}

				var submeshPrimitive = accessors.subMeshPrimitives[submesh];
				prims[submesh] = new MeshPrimitive(submeshPrimitive, _root)
				{
					Material = ExportMaterial(materialsObj[submesh]),
				};
				accessors.subMeshPrimitives[submesh] = prims[submesh];
			}

			//remove any prims that have empty triangles
            nonEmptyPrims = new List<MeshPrimitive>(prims);
            nonEmptyPrims.RemoveAll(EmptyPrimitive);
            prims = nonEmptyPrims.ToArray();

            exportPrimitiveMarker.End();

			return prims;
		}

		// Blend Shapes / Morph Targets
		// Adopted from Gary Hsu (bghgary)
		// https://github.com/bghgary/glTF-Tools-for-Unity/blob/master/UnityProject/Assets/Gltf/Editor/Exporter.cs
		private void ExportBlendShapes(SkinnedMeshRenderer smr, Mesh meshObj, int submeshIndex, MeshPrimitive primitive, GLTFMesh mesh)
		{
			if (settings.BlendShapeExportProperties == GLTFSettings.BlendShapeExportPropertyFlags.None)
				return;

			if (_meshToBlendShapeAccessors.TryGetValue(meshObj, out var data))
			{
				mesh.Weights = data.weights;
				primitive.Targets = data.targets;
				primitive.TargetNames = data.targetNames;
				return;
			}

			if (smr != null && meshObj.blendShapeCount > 0)
			{
				List<Dictionary<string, AccessorId>> targets = new List<Dictionary<string, AccessorId>>(meshObj.blendShapeCount);
				List<Double> weights = new List<double>(meshObj.blendShapeCount);
				List<string> targetNames = new List<string>(meshObj.blendShapeCount);

#if UNITY_2019_3_OR_NEWER
				var meshHasNormals = meshObj.HasVertexAttribute(VertexAttribute.Normal);
				var meshHasTangents = meshObj.HasVertexAttribute(VertexAttribute.Tangent);
#else
				var meshHasNormals = meshObj.normals.Length > 0;
				var meshHasTangents = meshObj.tangents.Length > 0;
#endif

				for (int blendShapeIndex = 0; blendShapeIndex < meshObj.blendShapeCount; blendShapeIndex++)
				{
					exportBlendShapeMarker.Begin();

					targetNames.Add(meshObj.GetBlendShapeName(blendShapeIndex));
					// As described above, a blend shape can have multiple frames.  Given that glTF only supports a single frame
					// per blend shape, we'll always use the final frame (the one that would be for when 100% weight is applied).
					int frameIndex = meshObj.GetBlendShapeFrameCount(blendShapeIndex) - 1;

					var deltaVertices = new Vector3[meshObj.vertexCount];
					var deltaNormals = new Vector3[meshObj.vertexCount];
					var deltaTangents = new Vector3[meshObj.vertexCount];
					meshObj.GetBlendShapeFrameVertices(blendShapeIndex, frameIndex, deltaVertices, deltaNormals, deltaTangents);

					var exportTargets = new Dictionary<string, AccessorId>();

					if (!settings.BlendShapeExportSparseAccessors)
					{
						var positionAccessor = ExportAccessor(SchemaExtensions.ConvertVector3CoordinateSpaceAndCopy(deltaVertices, SchemaExtensions.CoordinateSpaceConversionScale));
						positionAccessor.Value.BufferView.Value.Target = BufferViewTarget.ArrayBuffer;
						exportTargets.Add(SemanticProperties.POSITION, positionAccessor);
					}
					else
					{
						// Debug.Log("Delta Vertices:\n"+string.Join("\n ", deltaVertices));
						// Debug.Log("Vertices:\n"+string.Join("\n ", meshObj.vertices));
						// Experimental: sparse accessor.
						// - get the accessor we want to base this upon
						// - this is how position is originally exported:
						//   ExportAccessor(SchemaExtensions.ConvertVector3CoordinateSpaceAndCopy(meshObj.vertices, SchemaExtensions.CoordinateSpaceConversionScale));
						var baseAccessor = _meshToPrims[meshObj].aPosition;
						var exportedAccessor = ExportSparseAccessor(null, null, SchemaExtensions.ConvertVector3CoordinateSpaceAndCopy(deltaVertices, SchemaExtensions.CoordinateSpaceConversionScale));
						if (exportedAccessor != null)
						{
							exportTargets.Add(SemanticProperties.POSITION, exportedAccessor);
						}
					}

					if (meshHasNormals && settings.BlendShapeExportProperties.HasFlag(GLTFSettings.BlendShapeExportPropertyFlags.Normal))
					{
						if (!settings.BlendShapeExportSparseAccessors)
						{
							var accessor = ExportAccessor(SchemaExtensions.ConvertVector3CoordinateSpaceAndCopy(deltaNormals, SchemaExtensions.CoordinateSpaceConversionScale));
							accessor.Value.BufferView.Value.Target = BufferViewTarget.ArrayBuffer;
							exportTargets.Add(SemanticProperties.NORMAL, accessor);
						}
						else
						{
							var baseAccessor = _meshToPrims[meshObj].aNormal;
							exportTargets.Add(SemanticProperties.NORMAL, ExportSparseAccessor(null, null, SchemaExtensions.ConvertVector3CoordinateSpaceAndCopy(deltaVertices, SchemaExtensions.CoordinateSpaceConversionScale)));
						}
					}
					if (meshHasTangents && settings.BlendShapeExportProperties.HasFlag(GLTFSettings.BlendShapeExportPropertyFlags.Tangent))
					{
						if (!settings.BlendShapeExportSparseAccessors)
						{
							var accessor = ExportAccessor(SchemaExtensions.ConvertVector3CoordinateSpaceAndCopy(deltaTangents, SchemaExtensions.CoordinateSpaceConversionScale));
							accessor.Value.BufferView.Value.Target = BufferViewTarget.ArrayBuffer;
							exportTargets.Add(SemanticProperties.TANGENT, accessor);
						}
						else
						{
							// 	var baseAccessor = _meshToPrims[meshObj].aTangent;
							// 	exportTargets.Add(SemanticProperties.TANGENT, ExportSparseAccessor(baseAccessor, SchemaExtensions.ConvertVector4CoordinateSpaceAndCopy(meshObj.tangents, SchemaExtensions.TangentSpaceConversionScale), SchemaExtensions.ConvertVector4CoordinateSpaceAndCopy(deltaVertices, SchemaExtensions.TangentSpaceConversionScale)));
							exportTargets.Add(SemanticProperties.TANGENT, ExportAccessor(SchemaExtensions.ConvertVector3CoordinateSpaceAndCopy(deltaTangents, SchemaExtensions.CoordinateSpaceConversionScale)));
							// Debug.LogWarning("Blend Shape Tangents for " + meshObj + " won't be exported with sparse accessors – sparse accessor for tangents isn't supported right now.");
						}
					}
					targets.Add(exportTargets);

					// We need to get the weight from the SkinnedMeshRenderer because this represents the currently
					// defined weight by the user to apply to this blend shape.  If we instead got the value from
					// the unityMesh, it would be a _per frame_ weight, and for a single-frame blend shape, that would
					// always be 100.  A blend shape might have more than one frame if a user wanted to more tightly
					// control how a blend shape will be animated during weight changes (e.g. maybe they want changes
					// between 0-50% to be really minor, but between 50-100 to be extreme, hence they'd have two frames
					// where the first frame would have a weight of 50 (meaning any weight between 0-50 should be relative
					// to the values in this frame) and then any weight between 50-100 would be relevant to the weights in
					// the second frame.  See Post 20 for more info:
					// https://forum.unity3d.com/threads/is-there-some-method-to-add-blendshape-in-editor.298002/#post-2015679
					if(exportTargets.Any())
						weights.Add(smr.GetBlendShapeWeight(blendShapeIndex) / 100);

					exportBlendShapeMarker.End();
				}

				if(weights.Any() && targets.Any())
				{
					mesh.Weights = weights;
					primitive.Targets = targets;
					primitive.TargetNames = targetNames;
				}
				else
				{
					mesh.Weights = null;
					primitive.Targets = null;
					primitive.TargetNames = null;
				}

				// cache the exported data; we can re-use it between all submeshes of a mesh.
				_meshToBlendShapeAccessors.Add(meshObj, new BlendShapeAccessors()
				{
					targets = targets,
					weights = weights,
					targetNames = targetNames
				});
			}
		}

		private static bool EmptyPrimitive(MeshPrimitive prim)
		{
			if (prim == null || prim.Attributes == null)
			{
				return true;
			}
			return false;
		}

		private static DrawMode GetDrawMode(MeshTopology topology)
		{
			switch (topology)
			{
				case MeshTopology.Points: return DrawMode.Points;
				case MeshTopology.Lines: return DrawMode.Lines;
				case MeshTopology.LineStrip: return DrawMode.LineStrip;
				case MeshTopology.Triangles: return DrawMode.Triangles;
			}

			throw new Exception("glTF does not support Unity mesh topology: " + topology);
		}

#if UNITY_EDITOR
		private const string MakeMeshReadableDialogueDecisionKey = nameof(MakeMeshReadableDialogueDecisionKey);
		private static PropertyInfo canAccessProperty =
			typeof(Mesh).GetProperty("canAccess", BindingFlags.Instance | BindingFlags.Default | BindingFlags.NonPublic);
#endif

		private static bool MeshIsReadable(Mesh mesh)
		{
#if UNITY_EDITOR
			return mesh.isReadable || (bool) (canAccessProperty?.GetMethod?.Invoke(mesh, null) ?? true);
#else
			return mesh.isReadable;
#endif
		}
	}
}
