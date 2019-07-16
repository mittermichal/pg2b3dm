using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using B3dm.Tile;
using B3dm.Tileset;
using CommandLine;
using Newtonsoft.Json;
using Npgsql;
using SharpGLTF.Geometry;
using SharpGLTF.Materials;
using SharpGLTF.Schema2;
using Wkb2Gltf;
using Wkx;
using VERTEX = SharpGLTF.Geometry.VertexTypes.VertexPositionNormal;


namespace pg2b3dm
{
    class Program
    {
        static string password = string.Empty;
        static int counter = 1;

        static void Main(string[] args)
        {
            var version = Assembly.GetEntryAssembly().GetName().Version;
            Console.WriteLine($"tool: pg2b3dm {version}");

            Parser.Default.ParseArguments<Options>(args).WithParsed(o => {
                o.User = string.IsNullOrEmpty(o.User) ? Environment.UserName : o.User;
                o.Database = string.IsNullOrEmpty(o.Database) ? Environment.UserName : o.Database;

                var connectionString = $"Host={o.Host};Username={o.User};Database={o.Database};Port={o.Port}";
                var istrusted = TrustedConnectionChecker.HasTrustedConnection(connectionString);

                if (!istrusted) {
                    connectionString = $"Host={o.Host};Username={o.User};Password={password};Database={o.Database};Port={o.Port}";
                    Console.Write($"Password for user {o.User}: ");
                    password = PasswordAsker.GetPassword();
                    Console.WriteLine();
                }

                Console.WriteLine($"Start processing....");

                var stopWatch = new Stopwatch();
                stopWatch.Start();

                // Do in try catch as may not have acces rights.
                string output = o.Output;
                string outputTiles = output + "/tiles";
                if (!Directory.Exists(output)) {
                    Directory.CreateDirectory(output);
                }
                if (!Directory.Exists(outputTiles)) {
                    Directory.CreateDirectory(outputTiles);
                }

                var geometryTable = o.GeometryTable;
                var geometryColumn = o.GeometryColumn;
                Console.WriteLine("Calculating bounding boxes...");
                var conn = new NpgsqlConnection(connectionString);
                conn.Open();
                var bbox3d = BoundingBoxRepository.GetBoundingBox3D(conn, geometryTable, geometryColumn);

                var translation = bbox3d.GetCenter().ToVector();
                var zupBoxes = GetZupBoxes(conn, geometryTable, geometryColumn, translation);
                var tree = TileCutter.ConstructTree(zupBoxes);

                Console.WriteLine("Writing tileset.json...");
                WiteTilesetJson(translation, tree, o.Output);

                Console.WriteLine($"Writing {Counter.Instance.Count} tiles...");

                List<Task> tasks = new List<Task>();
                WriteTiles(conn, geometryTable, geometryColumn, translation, tree, o.Output, tasks);
                Task.WaitAll(tasks.ToArray());
                conn.Close();
                stopWatch.Stop();
                Console.WriteLine();
                Console.WriteLine($"Elapsed: {stopWatch.ElapsedMilliseconds / 1000} seconds");
                Console.WriteLine("Program finished.");
            });
        }

        private static void WriteTiles(NpgsqlConnection conn, string geometryTable, string geometryColumn, double[] translation, B3dm.Tileset.Node node, string outputPath, List<Task> tasks)
        {
            if (node.Features.Count > 0) {
                counter++;
                var subset = (from f in node.Features select (f.Id)).ToArray();
                var geometries = BoundingBoxRepository.GetGeometrySubset(conn, geometryTable, geometryColumn, translation, subset);

                // WriteB3dm(geometries, node.Id);
                var state = new StateInfo() { Geometries = geometries, TileId = node.Id, OutputPath = outputPath };
                var backgroundTask = new Task(() => WriteB3dmBackgroundTask(state));
                backgroundTask.Start();
                tasks.Add(backgroundTask);
            }
            // and write children too
            foreach (var subnode in node.Children) {
               var perc = Math.Round(((double)counter / Counter.Instance.Count) * 100,2);
                Console.Write($"\rProgress: tile {counter} - {perc.ToString("F")}%");
                WriteTiles(conn, geometryTable, geometryColumn, translation, subnode, outputPath, tasks);
            }
        }

        private static void WiteTilesetJson(double[] translation, B3dm.Tileset.Node tree, string outputPath)
        {
            var tileset = TreeSerializer.ToTileset(tree, translation);
            var s = JsonConvert.SerializeObject(tileset, Formatting.Indented, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore });
            File.WriteAllText($"{outputPath}/tileset.json", s);
        }

        private static List<BoundingBox3D> GetZupBoxes(NpgsqlConnection conn, string GeometryTable, string GeometryColumn, double[] translation)
        {
            var bboxes = BoundingBoxRepository.GetAllBoundingBoxes(conn, GeometryTable, GeometryColumn, translation);
            var zupBoxes = new List<BoundingBox3D>();
            foreach (var bbox in bboxes) {
                var zupBox = bbox.TransformYToZ();
                zupBoxes.Add(zupBox);
            }

            return zupBoxes;
        }

        private static void WriteB3dmBackgroundTask(Object stateInfo)
        {
            WriteB3dm(((StateInfo)stateInfo).Geometries, ((StateInfo)stateInfo).TileId, ((StateInfo)stateInfo).OutputPath);
        }

        private static void WriteB3dm(List<GeometryRecord> geomrecords, int tile_id, string outputPath)
        {
            var triangleCollection = new TriangleCollection();
            foreach(var g in geomrecords) {
                var surface = (PolyhedralSurface)g.Geometry;
                var triangles = Triangulator.GetTriangles(surface);
                triangleCollection.AddRange(triangles);
            }


            var materialRed = new MaterialBuilder().
                WithDoubleSide(true).
                WithMetallicRoughnessShader().
                WithChannelParam("BaseColor", new Vector4(1, 0, 0, 1));

            var materialGreen = new MaterialBuilder().
                WithDoubleSide(true).
                WithMetallicRoughnessShader().
                WithChannelParam("BaseColor", new Vector4(0, 1, 0, 1));


            var mesh = new MeshBuilder<VERTEX>("mesh");

            var prim = mesh.UsePrimitive(materialGreen);

            foreach (var triangle in triangleCollection) {
                var normal = triangle.GetNormal();
                // todo: use some formula for this...
                if(normal.Y > 0 && normal.X > -0.1) {
                    prim = mesh.UsePrimitive(materialRed);
                }
                else {
                    prim = mesh.UsePrimitive(materialGreen);
                }

                prim.AddTriangle(
                    new VERTEX((float)triangle.GetP0().X, (float)triangle.GetP0().Y, (float)triangle.GetP0().Z, normal.X, normal.Y, normal.Z),
                    new VERTEX((float)triangle.GetP1().X, (float)triangle.GetP1().Y, (float)triangle.GetP1().Z, normal.X, normal.Y, normal.Z),
                    new VERTEX((float)triangle.GetP2().X, (float)triangle.GetP2().Y, (float)triangle.GetP2().Z, normal.X, normal.Y, normal.Z)
                    );
            }

            var model = ModelRoot.CreateModel();
            model.CreateMeshes(mesh);
            model.UseScene("Default")
                .CreateNode()
                .WithMesh(model.LogicalMeshes[0]);
            var bytes = model.WriteGLB().Array;
            var b3dm = new B3dm.Tile.B3dm(bytes);
            B3dmWriter.WriteB3dm($"{outputPath}/tiles/{tile_id}.b3dm", b3dm);
        }
    }

    public class StateInfo
    {
        public List<GeometryRecord> Geometries { get; set; }
        public int TileId { get; set; }
        public string OutputPath { get; set; }
    }
}
