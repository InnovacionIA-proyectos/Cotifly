using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Correo.Exceptions;
using Cotifly.Aplicacion.Correo.Interfaces;
using Cotifly.Aplicacion.Correo.Services;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Infraestructura.Comun;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Correo.Services;
using Cotifly.Infraestructura.Pdf.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Correo;

/// <summary>
/// Registro y configuración de PDF y correo (#11): adaptadores por defecto, <c>Correo:Modo</c> (solo <c>smtp_relay</c>
/// implementado, D3), <c>Correo:Habilitado=false</c> por defecto, <c>Pdf:StorageModo</c> (S3 diferido, ADR-012) y la fecha
/// de negocio en la zona configurada.
/// </summary>
public sealed class clsRegistroPdfYCorreoTests
{
    private static ServiceProvider Proveedor(IDictionary<string, string?>? configuracion = null, IClsReloj? reloj = null) =>
        clsServiciosPruebas.Crear(null, reloj ?? new clsRelojFalso(), configuracionExtra: configuracion);

    private static clsMensajeCorreo Mensaje() =>
        new(null, ["a@x.com"], [], "Asunto", "Cuerpo", "<p>Cuerpo</p>", new clsAdjuntoCorreo("PCN-1.pdf", [1]));

    [Fact]
    public void PorDefectoElCorreoEstaDeshabilitadoYElModoEsElRelay()
    {
        using var proveedor = Proveedor();

        proveedor.GetRequiredService<IOptions<clsOpcionesCorreo>>().Value.Habilitado.Should().BeFalse();
        proveedor.GetRequiredService<IOptions<clsOpcionesCorreoTransporte>>().Value.ModoNormalizado.Should().Be("smtp_relay");
        proveedor.GetRequiredService<IClsTransporteCorreo>().Should().BeOfType<clsTransporteSmtpRelay>();
        proveedor.GetRequiredService<IClsGeneradorPdf>().Should().BeOfType<clsGeneradorPdfMigraDoc>();
        proveedor.GetRequiredService<IClsAlmacenPdf>().Should().BeOfType<clsAlmacenPdfVolumen>();
        proveedor.GetRequiredService<IClsCalendarioNegocio>().Should().BeOfType<clsCalendarioNegocio>();
    }

    [Theory]
    [InlineData("wscorreos")]
    [InlineData("smtp")]
    public async Task LosModosNoPortadosFallanComoEnvioFallido(string modo)
    {
        using var proveedor = Proveedor(new Dictionary<string, string?> { ["Correo:Modo"] = modo });

        var transporte = proveedor.GetRequiredService<IClsTransporteCorreo>();
        var enviar = () => transporte.EnviarAsync(Mensaje(), TestContext.Current.CancellationToken);

        transporte.Modo.Should().Be(modo);
        (await enviar.Should().ThrowAsync<clsEnvioCorreoException>()).Which.Message.Should().Contain("D3");
    }

    [Theory]
    [InlineData(true, "smtp_relay", "10.10.40.62", null)]
    [InlineData(false, "wscorreos", null, null)]
    [InlineData(false, "", null, null)]
    [InlineData(true, "wscorreos", "10.10.40.62", "no está implementado")]
    [InlineData(true, "smtp", "10.10.40.62", "no está implementado")]
    [InlineData(true, "smtp_relay", null, "SmtpRelay:Host")]
    [InlineData(false, "sendgrid", null, "no es válido")]
    public void LaConfiguracionDelCorreoSeValidaSegunElModo(bool habilitado, string modo, string? host, string? error)
    {
        var resultado = new clsValidadorOpcionesCorreoTransporte().Validate(null, new clsOpcionesCorreoTransporte
        {
            Habilitado = habilitado,
            Modo = modo,
            SmtpRelay = new clsOpcionesSmtpRelay { Host = host },
        });

        if (error is null)
        {
            resultado.Succeeded.Should().BeTrue(resultado.FailureMessage);
        }
        else
        {
            resultado.Failed.Should().BeTrue();
            resultado.FailureMessage.Should().Contain(error);
        }
    }

    [Fact]
    public void ElPuertoYElTiempoDelRelaySeValidan()
    {
        var resultado = new clsValidadorOpcionesCorreoTransporte().Validate(null, new clsOpcionesCorreoTransporte
        {
            Habilitado = true,
            SmtpRelay = new clsOpcionesSmtpRelay { Host = "relay", Port = 0, TimeoutSeg = 0 },
        });

        resultado.FailureMessage.Should().Contain("SmtpRelay:Port").And.Contain("SmtpRelay:TimeoutSeg");
    }

    [Theory]
    [InlineData("volumen", "/data/pdfs", null)]
    [InlineData(null, "/data/pdfs", null)]
    [InlineData("s3", "/data/pdfs", "ADR-012")]
    [InlineData("minio", "/data/pdfs", "no es válido")]
    [InlineData("volumen", " ", "StorageDir")]
    public void ElAlmacenSoloAdmiteElVolumen(string? modo, string carpeta, string? error)
    {
        var resultado = new clsValidadorOpcionesAlmacenPdf().Validate(null, new clsOpcionesAlmacenPdf { StorageModo = modo, StorageDir = carpeta });

        if (error is null)
        {
            resultado.Succeeded.Should().BeTrue();
        }
        else
        {
            resultado.FailureMessage.Should().Contain(error);
        }
    }

    [Fact]
    public void UnaConfiguracionInvalidaImpideArrancar()
    {
        using var proveedor = Proveedor(new Dictionary<string, string?>
        {
            ["Correo:Habilitado"] = "true",
            ["Correo:Modo"] = "wscorreos",
            ["Pdf:StorageModo"] = "s3",
        });

        var leerCorreo = () => proveedor.GetRequiredService<IOptions<clsOpcionesCorreoTransporte>>().Value;
        var leerPdf = () => proveedor.GetRequiredService<IOptions<clsOpcionesAlmacenPdf>>().Value;

        leerCorreo.Should().Throw<OptionsValidationException>();
        leerPdf.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void ElCorreoHabilitadoSeLeeDeLaMismaSeccion()
    {
        using var proveedor = Proveedor(new Dictionary<string, string?>
        {
            ["Correo:Habilitado"] = "true",
            ["Correo:SmtpRelay:Host"] = "10.10.40.62",
            ["Pdf:BaseUrl"] = "https://cotifly.example",
        });

        proveedor.GetRequiredService<IOptions<clsOpcionesCorreo>>().Value.Habilitado.Should().BeTrue();
        proveedor.GetRequiredService<IOptions<clsOpcionesCorreoTransporte>>().Value.SmtpRelay.Host.Should().Be("10.10.40.62");
        proveedor.GetRequiredService<IOptions<clsOpcionesPdf>>().Value.BaseUrl.Should().Be("https://cotifly.example");
    }

    [Theory]
    [InlineData("2026-09-25T03:00:00Z", "2026-09-24")]
    [InlineData("2026-09-25T05:00:00Z", "2026-09-25")]
    [InlineData("2026-12-31T23:30:00Z", "2026-12-31")]
    public void LaFechaDeNegocioEsLaDeBogotaAunqueElContenedorEsteEnUtc(string ahoraUtc, string hoy)
    {
        var reloj = new clsRelojFalso(DateTimeOffset.Parse(ahoraUtc, System.Globalization.CultureInfo.InvariantCulture));

        var calendario = new clsCalendarioNegocio(reloj, Options.Create(new clsOpcionesZona()));

        calendario.Hoy.Should().Be(DateOnly.Parse(hoy, System.Globalization.CultureInfo.InvariantCulture));
    }
}
