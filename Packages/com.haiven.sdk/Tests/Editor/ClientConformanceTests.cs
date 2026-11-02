using System;
using System.Collections.Generic;
using System.IO;
using Haiven.Sdk.Json;
using NUnit.Framework;

namespace Haiven.Sdk.Tests
{
    /// <summary>
    /// Runs the shared gaming-client/v0 scripted-server corpus inside Unity's
    /// EditMode runner against the exact package source Unity compiles.
    /// </summary>
    public class ClientConformanceTests
    {
        private const string Runner = "unity-editor";
        private const string Profile = "gaming/unity";

        [Test] public void GamingClientCorpusPasses()
        {
            var path = Environment.GetEnvironmentVariable("HAIVEN_CLIENT_SUITE");
            if (string.IsNullOrEmpty(path))
            {
                throw new InvalidOperationException("HAIVEN_CLIENT_SUITE must name the generated corpus");
            }
            if (!File.Exists(path)) throw new FileNotFoundException("client corpus does not exist", path);

            var suite = HaivenJson.Default.Parse(File.ReadAllText(path));
            var lifecycle = String(Property(suite, "lifecycle"));
            var scenarios = new List<IHaivenJsonNode>(Property(suite, "scenarios").EnumerateArray());
            var failures = new List<string>();
            var ran = 0;
            var waived = 0;

            foreach (var scenario in scenarios)
            {
                var id = String(Property(scenario, "id"));
                var waivers = Property(scenario, "waivers");
                if (HasProperty(waivers, Runner) || HasProperty(waivers, Profile) || HasProperty(waivers, lifecycle))
                {
                    waived++;
                    continue;
                }

                ran++;
                var failure = RunScenario(scenario);
                if (failure != null) failures.Add(id + ": " + failure);
            }

            if (ran == 0) throw new InvalidOperationException("the suite has no scenario this runner can run");
            if (ran + waived != scenarios.Count)
            {
                throw new InvalidOperationException("client corpus accounting did not cover every scenario");
            }
            if (failures.Count != 0) throw new InvalidOperationException(string.Join("\n", failures));

            var results = Environment.GetEnvironmentVariable("HAIVEN_UNITY_RESULTS");
            if (string.IsNullOrEmpty(results))
            {
                throw new InvalidOperationException("HAIVEN_UNITY_RESULTS must name the engine artifact directory");
            }
            Directory.CreateDirectory(results);
            File.WriteAllText(
                Path.Combine(results, "client-conformance.json"),
                "{\"ran\":" + ran + ",\"waived\":" + waived + ",\"total\":" + scenarios.Count + "}\n");
            Console.WriteLine(
                "HAIVEN_TEST_PASS client " + ran + " of " + scenarios.Count + " scenario(s); " + waived + " waived");
        }

        private static string RunScenario(IHaivenJsonNode scenario)
        {
            var socket = new RecordingSocket();
            var counter = 0;
            using (var session = new HaivenSessionCore(socket, () => "scripted-token", () => "rq-" + ++counter))
            {
                var delivered = new List<ServerFrame>();
                session.FrameReceived += received => delivered.Add(received.Frame);
                var bound = new Dictionary<string, string>();
                var read = 0;
                var seen = 0;
                var index = 0;

                foreach (var step in Property(scenario, "steps").EnumerateArray())
                {
                    index++;
                    var verb = String(Property(step, "do"));
                    var at = "step " + index + " (" + verb + ")";
                    switch (verb)
                    {
                        case "open":
                            session.Opened();
                            break;
                        case "call":
                            IHaivenJsonNode payload;
                            session.Send(
                                String(Property(step, "type")),
                                step.TryGetProperty("payload", out payload) ? Resolve(payload, bound) : null);
                            break;
                        case "serve":
                            socket.Receive(Resolve(Property(step, "frame"), bound).ToJson());
                            break;
                        case "expectSent":
                            if (read >= socket.Sent.Count)
                            {
                                return at + ": expected a " + String(Property(step, "type")) + " and nothing was written";
                            }

                            var sent = HaivenJson.Default.Parse(socket.Sent[read++]);
                            var actualType = String(Property(sent, "type"));
                            var expectedType = String(Property(step, "type"));
                            if (actualType != expectedType) return at + ": wrote " + actualType;
                            var requestId = String(Property(sent, "requestId"));
                            IHaivenJsonNode bind;
                            if (step.TryGetProperty("bind", out bind)) bound[String(bind)] = requestId;
                            IHaivenJsonNode same;
                            if (step.TryGetProperty("same", out same))
                            {
                                var alias = String(same);
                                string expected;
                                if (!bound.TryGetValue(alias, out expected) || expected != requestId)
                                {
                                    return at + ": wrote " + requestId + ", not " + alias;
                                }
                            }
                            break;
                        case "expectQuiet":
                            if (read < socket.Sent.Count) return at + ": also wrote " + socket.Sent[read];
                            break;
                        case "expectState":
                            var state = session.State.ToString().ToLowerInvariant();
                            if (state != String(Property(step, "state"))) return at + ": is " + state;
                            break;
                        case "expectDelivered":
                            var type = String(Property(step, "type"));
                            var found = seen;
                            while (found < delivered.Count && delivered[found].Type != type) found++;
                            if (found == delivered.Count) return at + ": no " + type + " was handed to the caller";
                            var frame = delivered[found];
                            seen = found + 1;
                            IHaivenJsonNode error;
                            if (step.TryGetProperty("error", out error) && frame.Error != String(error))
                            {
                                return at + ": error " + frame.Error;
                            }
                            break;
                        default:
                            return at + ": unknown step";
                    }
                }
            }

            return null;
        }

        /// <summary>Replaces every {"ref": alias} with the request id bound to that alias.</summary>
        private static IHaivenJsonNode Resolve(IHaivenJsonNode node, IReadOnlyDictionary<string, string> bound)
        {
            return HaivenJson.Default.Parse(HaivenJson.Default.Write(writer => WriteResolved(writer, node, bound)));
        }

        private static void WriteResolved(
            IHaivenJsonWriter writer,
            IHaivenJsonNode node,
            IReadOnlyDictionary<string, string> bound)
        {
            if (node.Kind == HaivenJsonKind.Object)
            {
                var members = new List<KeyValuePair<string, IHaivenJsonNode>>(node.EnumerateObject());
                if (members.Count == 1 && members[0].Key == "ref")
                {
                    writer.WriteString(bound[String(members[0].Value)]);
                    return;
                }

                writer.WriteStartObject();
                foreach (var member in members)
                {
                    writer.WritePropertyName(member.Key);
                    WriteResolved(writer, member.Value, bound);
                }
                writer.WriteEndObject();
                return;
            }

            if (node.Kind == HaivenJsonKind.Array)
            {
                writer.WriteStartArray();
                foreach (var item in node.EnumerateArray()) WriteResolved(writer, item, bound);
                writer.WriteEndArray();
                return;
            }

            writer.WriteNode(node);
        }

        private static IHaivenJsonNode Property(IHaivenJsonNode node, string name)
        {
            IHaivenJsonNode value;
            if (!node.TryGetProperty(name, out value)) throw new InvalidDataException("missing property: " + name);
            return value;
        }

        private static bool HasProperty(IHaivenJsonNode node, string name)
        {
            IHaivenJsonNode ignored;
            return node.TryGetProperty(name, out ignored);
        }

        private static string String(IHaivenJsonNode node)
        {
            return node.GetString();
        }

        private sealed class RecordingSocket : IHaivenSocket
        {
            internal readonly List<string> Sent = new List<string>();

            public event Action<string> Received = delegate { };
            public event Action<int, string> Closed = delegate { };

            public void Send(string payload)
            {
                Sent.Add(payload);
            }

            public void Close()
            {
                Closed(1000, string.Empty);
            }

            internal void Receive(string payload)
            {
                Received(payload);
            }
        }
    }
}
