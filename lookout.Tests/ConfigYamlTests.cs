using Xunit;

namespace Lookout.Tests
{
    public class ConfigYamlTests
    {
        static readonly string Sample = """
            mqtt:
              # broker
              host: 10.0.0.1
              password: hunter2
            telegram:
              token: 'abc:def'
              chatids:
                - '-1001'
            options:
              timeoffset: 120
            """.Replace("\r\n", "\n");

        [Fact]
        public void MaskSecrets_ReplacesPasswordsAndKeepsComments()
        {
            string masked = ConfigYaml.MaskSecrets(Sample);
            Assert.Contains("# broker", masked);
            Assert.Contains("host: 10.0.0.1", masked);
            Assert.DoesNotContain("hunter2", masked);
            Assert.DoesNotContain("abc:def", masked);
            Assert.Contains("password: \"" + ConfigYaml.Mask + "\"", masked);
            Assert.Contains("token: \"" + ConfigYaml.Mask + "\"", masked);
        }

        [Fact]
        public void RestoreSecrets_PutsMaskedValuesBack()
        {
            string masked = ConfigYaml.MaskSecrets(Sample);
            string restored = ConfigYaml.RestoreSecrets(masked, Sample);
            Assert.Contains("hunter2", restored);
            Assert.Contains("abc:def", restored);
            Assert.Contains("# broker", restored);
        }

        [Fact]
        public void RestoreSecrets_KeepsAPasswordThatWasEdited()
        {
            string edited = ConfigYaml.MaskSecrets(Sample).Replace("password: \"" + ConfigYaml.Mask + "\"", "password: newpass");
            string restored = ConfigYaml.RestoreSecrets(edited, Sample);
            Assert.Contains("newpass", restored);
            Assert.DoesNotContain("hunter2", restored);
        }

        [Fact]
        public void Set_ReplacesAnExistingScalarAndKeepsTheComment()
        {
            string next = ConfigYaml.Set(Sample, "mqtt.host", "10.0.0.2");
            Assert.Contains("host: 10.0.0.2", next);
            Assert.Contains("# broker", next);
            Assert.Contains("hunter2", next);
        }

        [Fact]
        public void Set_InsertsAMissingKey()
        {
            string next = ConfigYaml.Set(Sample, "options.buttons", "true");
            Assert.Contains("buttons: true", next);
            Assert.Contains("timeoffset: 120", next);
        }

        [Fact]
        public void Apply_SkipsMaskedSecrets()
        {
            string next = ConfigYaml.Apply(Sample, new Dictionary<string, string>
            {
                ["mqtt.host"] = "10.9.9.9",
                ["mqtt.password"] = ConfigYaml.Mask
            });
            Assert.Contains("host: 10.9.9.9", next);
            Assert.Contains("hunter2", next);
        }

        [Fact]
        public void Set_ReplacesAScalarSequence()
        {
            string next = ConfigYaml.Set(Sample, "telegram.chatids", "-1, -2");
            Assert.Contains("[-1, -2]", next);
        }
    }
}
