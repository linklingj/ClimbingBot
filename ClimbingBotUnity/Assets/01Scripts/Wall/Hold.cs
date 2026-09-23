using UnityEngine;

namespace ClimbingBot
{
    public enum HoldRole
    {
        Normal,
        Start,
        Top
    }

    [RequireComponent(typeof(MeshRenderer))]
    public class Hold : MonoBehaviour
    {
        [Tooltip("Unique within one scene. VLM, RL and AR all refer to a hold by this id.")]
        public int id;

        public HoldRole role = HoldRole.Normal;
        public Color color = Color.gray;

        [Tooltip("Wall-local 2D position in meters, origin at the wall's bottom-left corner.")]
        public Vector2 wallPosition;

        void Awake()
        {
            ApplyColor();
        }

        void OnValidate()
        {
            ApplyColor();
        }

        // A MaterialPropertyBlock is not serialized, so this has to run again on every load.
        public void ApplyColor()
        {
            var block = new MaterialPropertyBlock();
            var renderer = GetComponent<MeshRenderer>();
            renderer.GetPropertyBlock(block);
            block.SetColor("_BaseColor", color);
            renderer.SetPropertyBlock(block);
        }
    }
}
