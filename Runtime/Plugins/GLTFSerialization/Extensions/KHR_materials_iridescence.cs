﻿using System;
using GLTF.Extensions;
using Newtonsoft.Json.Linq;
using Color = GLTF.Math.Color;

namespace GLTF.Schema
{
	// https://github.com/ux3d/glTF/tree/extensions/KHR_materials_iridescence/extensions/2.0/Khronos/KHR_materials_iridescence
	[Serializable]
	public class KHR_materials_iridescence : IExtension
	{
		public float iridescenceFactor = 0f;
		public TextureInfo iridescenceTexture; // R channel
		public float iridescenceIor = 1.3f;
		public float iridescenceThicknessMinimum = 100.0f;
		public float iridescenceThicknessMaximum = 400.0f;
		public TextureInfo iridescenceThicknessTexture; // G channel

		public static readonly Color COLOR_DEFAULT = Color.White;

		public JProperty Serialize()
		{
			var jo = new JObject();
			JProperty jProperty = new JProperty(KHR_materials_iridescence_Factory.EXTENSION_NAME, jo);

			jo.Add(new JProperty(nameof(iridescenceFactor), iridescenceFactor));
			jo.Add(new JProperty(nameof(iridescenceIor), iridescenceIor));
			jo.Add(new JProperty(nameof(iridescenceIor), iridescenceIor));
			jo.Add(new JProperty(nameof(iridescenceThicknessMinimum), iridescenceThicknessMaximum));
			if(iridescenceTexture != null) {
				jo.Add(new JProperty(nameof(iridescenceTexture),
						new JObject(
							new JProperty(TextureInfo.INDEX, iridescenceTexture.Index.Id),
							new JProperty(TextureInfo.TEXCOORD, iridescenceTexture.TexCoord)
						)
					)
				);
			}
			if(iridescenceThicknessTexture != null) {
				jo.Add(new JProperty(nameof(iridescenceThicknessTexture),
						new JObject(
							new JProperty(TextureInfo.INDEX, iridescenceThicknessTexture.Index.Id),
							new JProperty(TextureInfo.TEXCOORD, iridescenceThicknessTexture.TexCoord)
						)
					)
				);
			}
			return jProperty;
		}

		public IExtension Clone(GLTFRoot root)
		{
			return new KHR_materials_iridescence()
			{
				iridescenceFactor = iridescenceFactor, iridescenceIor = iridescenceIor,
				iridescenceThicknessMinimum = iridescenceThicknessMinimum, iridescenceThicknessMaximum = iridescenceThicknessMaximum,
				iridescenceTexture = iridescenceTexture, iridescenceThicknessTexture = iridescenceThicknessTexture,
			};
		}
	}

	public class KHR_materials_iridescence_Factory : ExtensionFactory
	{
		public const string EXTENSION_NAME = "KHR_materials_iridescence";

		public KHR_materials_iridescence_Factory()
		{
			ExtensionName = EXTENSION_NAME;
		}

		public override IExtension Deserialize(GLTFRoot root, JProperty extensionToken)
		{
			if (extensionToken != null)
			{
				var extension = new KHR_materials_iridescence();
				extension.iridescenceFactor           = extensionToken.Value[nameof(KHR_materials_iridescence.iridescenceFactor)]?.Value<float>() ?? 0;
				extension.iridescenceIor              = extensionToken.Value[nameof(KHR_materials_iridescence.iridescenceIor)]?.Value<float>() ?? 1.3f;
				extension.iridescenceThicknessMinimum = extensionToken.Value[nameof(KHR_materials_iridescence.iridescenceIor)]?.Value<float>() ?? 100f;
				extension.iridescenceThicknessMaximum = extensionToken.Value[nameof(KHR_materials_iridescence.iridescenceIor)]?.Value<float>() ?? 400f;
				extension.iridescenceTexture          = extensionToken.Value[nameof(KHR_materials_iridescence.iridescenceTexture)]?.DeserializeAsTexture(root);
				extension.iridescenceThicknessTexture = extensionToken.Value[nameof(KHR_materials_iridescence.iridescenceThicknessTexture)]?.DeserializeAsTexture(root);
				return extension;
			}

			return null;
		}
	}
}
