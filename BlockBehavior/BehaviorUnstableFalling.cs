using System.Collections.Generic;
using Vintagestory.API;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

#nullable disable

namespace Vintagestory.GameContent
{
    /// <summary>
    /// Spawns an EntityBlockFalling when the user places a block that has air underneath it or if a neighbor block is
    /// removed and causes air to be underneath it. Also has optional functionality to prevent a block being placed if it is unstable.
    /// Uses the code "UnstableFalling".
    /// </summary>
    [DocumentAsJson]
    [AddDocumentationProperty("AttachableFaces", "The faces that this block could be attached from which will prevent it from falling.", "System.String[]", "Optional", "None")]
    [AddDocumentationProperty("AttachmentAreas", "A list of attachment areas per face that determine what blocks can be attached to.", "System.Collections.Generic.Dictionary{System.String,Vintagestory.API.Datastructures.RotatableCube}", "Optional", "None")]
    [AddDocumentationProperty("AttachmentArea", "A single attachment area that determine what blocks can be attached to. Used if AttachmentAreas is not supplied.", "Vintagestory.API.Mathtools.Cuboidi", "Optional", "None")]
    [AddDocumentationProperty("AllowUnstablePlacement", "Can this block be placed in an unstable position?", "System.Boolean", "Optional", "False", true)]
    [AddDocumentationProperty("IgnorePlaceTest", "(Obsolete) Please use the AllowUnstablePlacement attribute instead.", "System.Boolean", "Obsolete", "", false)]
    public class BlockBehaviorUnstableFalling : BlockBehavior
    {
        /// <summary>
        /// A list of block types which this block can always be attached to, regardless if there is a correct attachment area.
        /// </summary>
        [DocumentAsJson("Optional", "None")]
        AssetLocation[] exceptions;

        /// <summary>
        /// Can this block fall horizontally?
        /// </summary>
        [DocumentAsJson("Optional", "False")]
        public bool fallSideways;

        /// <summary>
        /// A multiplier for the number of dust particles for the falling block. A value of 0 means no dust particles.
        /// </summary>
        [DocumentAsJson("Optional", "0")]
        float dustIntensity;

        /// <summary>
        /// If <see cref="fallSideways"/> is enabled, this is the chance that the block will fall sideways instead of straight down.
        /// </summary>
        [DocumentAsJson("Optional", "0.3")]
        float fallSidewaysChance = 0.3f;

        /// <summary>
        /// The path to the sound to play when the block falls.
        /// </summary>
        [DocumentAsJson("Optional", "None")]
        AssetLocation fallSound;

        /// <summary>
        /// A multiplier of damage dealt to an entity when hit by the falling block. Damage depends on falling height.
        /// </summary>
        [DocumentAsJson("Optional", "1")]
        float impactDamageMul;

        /// <summary>
        /// A set of attachment areas for the unstable block. 
        /// </summary>
        Cuboidi[] attachmentAreas;

        /// <summary>
        /// The faces that this block could be attached from which will prevent it from falling.
        /// </summary>
        BlockFacing[] attachableFaces;

        /// <summary>
        /// Alternate block to spawn as falling block entity. Useful if the block changes after falling.
        /// </summary>
        [DocumentAsJson("Optional", "None")]
        AssetLocation variantAfterFalling;

        public BlockBehaviorUnstableFalling(Block block) : base(block)
        {
        }

        public override void Initialize(JsonObject properties)
        {
            base.Initialize(properties);

            attachableFaces = null;

            if (properties["attachableFaces"].Exists)
            {
                string[] faces = properties["attachableFaces"].AsArray<string>();
                attachableFaces = new BlockFacing[faces.Length];

                for (int i = 0; i < faces.Length; i++)
                {
                    attachableFaces[i] = BlockFacing.FromCode(faces[i]);
                }
            }

            var areas = properties["attachmentAreas"].AsObject<Dictionary<string, RotatableCube>>(null);
            attachmentAreas = new Cuboidi[6];
            if (areas != null)
            {
                foreach (var val in areas)
                {
                    val.Value.Origin.Set(8, 8, 8);
                    BlockFacing face = BlockFacing.FromFirstLetter(val.Key[0]);
                    attachmentAreas[face.Index] = val.Value.RotatedCopy().ConvertToCuboidi();
                }
            }
            else
            {
                attachmentAreas[4] = properties["attachmentArea"].AsObject<Cuboidi>(null);
            }

            exceptions = properties["exceptions"].AsObject(System.Array.Empty<AssetLocation>(), block.Code.Domain);
            fallSideways = properties["fallSideways"].AsBool(false);
            dustIntensity = properties["dustIntensity"].AsFloat(0);

            fallSidewaysChance = properties["fallSidewaysChance"].AsFloat(0.3f);
            string sound = properties["fallSound"].AsString(null);
            if (sound != null)
            {
                fallSound = AssetLocation.Create(sound, block.Code.Domain);
            }

            impactDamageMul = properties["impactDamageMul"].AsFloat(1f);

            string variantCode = properties["variantAfterFalling"].AsString(null);
            if (variantCode != null)
            {
                variantAfterFalling = AssetLocation.Create(variantCode, block.Code.Domain);
            }
        }

        public override bool CanPlaceBlock(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref EnumHandling handling, ref string failureCode)
        {
            handling = EnumHandling.PassThrough;
            if (block.Attributes?["allowUnstablePlacement"].AsBool() == true) return true;

            Cuboidi attachmentArea = attachmentAreas[4];

            BlockPos pos = blockSel.Position.DownCopy();
            Block onBlock = world.BlockAccessor.GetBlock(pos);
            if (blockSel != null &&
                !IsAttached(world.BlockAccessor, blockSel.Position) &&
                !onBlock.CanAttachBlockAt(world.BlockAccessor, block, pos, BlockFacing.UP, attachmentArea) &&
                !onBlock.WildCardMatch(exceptions))
            {
                handling = EnumHandling.PreventSubsequent;
                failureCode = "requiresolidground";
                return false;
            }

            return true;
        }

        public override void OnBlockPlaced(IWorldAccessor world, BlockPos blockPos, ref EnumHandling handling)
        {
            TryFalling(world, blockPos, ref handling);
        }

        public override void OnNeighbourBlockChange(IWorldAccessor world, BlockPos pos, BlockPos neibpos, ref EnumHandling handling)
        {
            base.OnNeighbourBlockChange(world, pos, neibpos, ref handling);

            if (world.Side == EnumAppSide.Client) return;

            EnumHandling bla = EnumHandling.PassThrough;
            TryFalling(world, pos, ref bla);
        }

        private bool TryFalling(IWorldAccessor world, BlockPos pos, ref EnumHandling handling)
        {
            if (world.Side != EnumAppSide.Server) return false;
            if (!fallSideways && IsAttached(world.BlockAccessor, pos)) return false;

            ICoreServerAPI sapi = (world as IServerWorldAccessor).Api as ICoreServerAPI;
            if (!sapi.World.Config.GetBool("allowFallingBlocks")) return false;

            if (IsReplacableBeneath(world, pos) || (fallSideways && world.Rand.NextDouble() < fallSidewaysChance && IsReplacableBeneathAndSideways(world, pos)))
            {
                BlockPos ourPos = pos.Copy();
                // Must run a frame later. This method is called from OnBlockPlaced, but at this point - if this is a freshly settled falling block, then the BE does not have its full data yet (because EntityBlockFalling makes a SetBlock, then only calls FromTreeAttributes on the BE
                sapi.Event.EnqueueMainThreadTask(() =>
                {
                var currentBlock = world.BlockAccessor.GetBlock(ourPos);
                if (this.block != currentBlock) return; // Block was already removed

                // Prevents duplication
                Entity entity = world.GetNearestEntity(ourPos.ToVec3d().Add(0.5, 0.5, 0.5), 1, 1.5f, (e) =>
                {
                    return e is EntityBlockFalling ebf && ebf.initialPos.Equals(ourPos);
                });
                if (entity != null) return;

                var be = world.BlockAccessor.GetBlockEntity(ourPos);

                Block fallingBlock = this.block;
                if (variantAfterFalling != null)
                {
                    Block altBlock = world.BlockAccessor.GetBlock(variantAfterFalling);
                    if (altBlock != null && altBlock.Id != 0)
                    {
                        fallingBlock = altBlock;
                    }
                }

                EntityBlockF
