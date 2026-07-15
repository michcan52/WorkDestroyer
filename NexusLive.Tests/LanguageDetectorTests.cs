using NexusLive.Core.Inference;
using Xunit;

namespace NexusLive.Tests
{
    public class LanguageDetectorTests
    {
        [Theory]
        [InlineData("Hola equipo, cómo están? El commanager está funcionando.", "es")]
        [InlineData("tenemos un problema abierto en el servidor.", "es")]
        [InlineData("Hello team, how is everything going with the new release?", "en")]
        [InlineData("We have a database connection problem on the main server.", "en")]
        [InlineData("", "es")]
        [InlineData(null, "es")]
        public void Detect_SimpleText_ShouldDetectCorrectLanguage(string? text, string expectedLang)
        {
            // Act
            string result = LanguageDetector.Detect(text);

            // Assert
            Assert.Equal(expectedLang, result);
        }

        [Fact]
        public void Detect_MultiLineTranscript_ShouldDetectLastSegmentLanguage()
        {
            // Arrange
            string transcript = @"[10:00:00] Hello team, we are testing the language selector.
[10:01:00] Sí, ya está funcionando la base de datos de manera correcta.
What are the suggestions?";

            // Act
            string result = LanguageDetector.Detect(transcript);

            // Assert
            Assert.Equal("es", result); // Last line is Spanish

            // Arrange 2
            string transcript2 = @"[10:00:00] Hola equipo, bienvenidos a la sesión.
[10:01:00] Please make sure to check the commanager logs.
What are the suggestions?";

            // Act 2
            string result2 = LanguageDetector.Detect(transcript2);

            // Assert 2
            Assert.Equal("en", result2); // Last line is English
        }
    }
}
