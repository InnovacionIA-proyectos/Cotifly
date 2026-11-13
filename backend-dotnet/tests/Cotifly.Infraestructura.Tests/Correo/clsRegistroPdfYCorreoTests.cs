using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Correo.Interfaces;
using Cotifly.Aplicacion.Correo.Services;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Infraestructura.Comun;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Correo.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Cotifly.Infraestructura.Pdf.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Correo;

/// <summary>
/// Registro y configuración de PDF y correo (ADR-023): adaptadores por defecto, el transporte MailSender (único
/// modo), <c>Correo:Habilitado=false</c> por defecto, la protección obligatoria fuera
/// de Production, <c>Pdf:StorageModo</c> (S3 diferido, ADR-012) y la fecha de negocio en la zona configurada.
/// </summary>
public sealed class clsRegistroPdfYCorreoTests
{
    private static ServiceProvider Proveedor(IDictionary<string, string?>? configuracion = null, IClsReloj? reloj = null, string entorno = "Development") =>
        clsServiciosPruebas.Crear(null, reloj ?? new clsRelojFalso(), configuracionExtra: configuracion, entorno: entorno);

    [Fact]
    public void PorDefectoElCorreoEstaDeshabilitadoYElTransporteEsMailSender()
    {
        using var proveedor = Proveedor();
        using var ambito = proveedor.CreateScope();

        proveedor.GetRequiredService<IOptions<clsOpcionesCorreo>>().Value.Habilitado.Should().BeFalse();
        proveedor.GetRequiredService<IOptions<clsOpcionesCorreo>>().Value.Proteccion.ModoNormalizado.Should().Be("redirigir");
        proveedor.GetRequiredService<IOptions<clsOpcionesCorreoTransporte>>().Value.ModoNormalizado.Should().Be("mailsender");
        ambito.ServiceProvider.GetRequiredService<IClsTransporteCorreo>().Should().BeOfType<clsTransporteMailSender>();
        ambito.ServiceProvider.GetRequiredService<IClsSeguimientoCorreoExterno>().Should().BeOfType<clsTransporteMailSender>();
        proveedor.GetRequiredService<IClsGeneradorPdf>().Should().BeOfType<clsGeneradorPdfMigraDoc>();
        proveedor.GetRequiredService<IClsAlmacenPdf>().Should().BeOfType<clsAlmacenPdfVolumen>();
        proveedor.GetRequiredService<IClsCalendarioNegocio>().Should().BeOfType<clsCalendarioNegocio>();
    }

    [Theory]
    [InlineData(true, "mailsender", "http://mailsender.local", null)]
    [InlineData(true, "", "http://mailsender.local", null)]
    [InlineData(false, "smtp_relay", null, null)]
    [InlineData(false, "wscorreos", null, null)]
    [InlineData(true, "smtp_relay", "http://mailsender.local", "ya no existe")]
    [InlineData(true, "wscorreos", "http://mailsender.local", "ya no existe")]
    [InlineData(true, "smtp", "http://mailsender.local", "ya no existe")]
    [InlineData(true, "mailsender", null, "MailSender:UrlBase")]
    [InlineData(true, "mailsender", "mailsender.local", "MailSender:UrlBase")]
    public void LaConfiguracionDelTransporteSeValida(bool habilitado, string modo, string? url, string? error)
    {
        var resultado = new clsValidadorOpcionesCorreoTransporte().Validate(null, new clsOpcionesCorreoTransporte
        {
            Habilitado = habilitado,
            Modo = modo,
            MailSender = new clsOpcionesMailSender { UrlBase = url },
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
    public void ElRemitenteElTiempoYLaPrioridadDeMailSenderSeValidan()
    {
        var resultado = new clsValidadorOpcionesCorreoTransporte().Validate(null, new clsOpcionesCorreoTransporte
        {
            Habilitado = true,
            MailSender = new clsOpcionesMailSender { UrlBase = "http://mailsender.local", Remitente = "sin-arroba", TimeoutSegundos = 0, Prioridad = "Urgente", ModoRemitente = "otro" },
        });

        resultado.FailureMessage.Should().Contain("MailSender:Remitente").And.Contain("MailSender:TimeoutSegundos").And.Contain("MailSender:Prioridad").And.Contain("MailSender:ModoRemitente");
    }

    /// <summary>
    /// El remitente corporativo debe pasar el formato estricto con que el transporte revisa el <c>From</c>; no basta con
    /// MailAddress (acepta un nombre con ángulos o un dominio de una etiqueta): con él, ningún correo saldría.
    /// </summary>
    [Theory]
    [InlineData("Notificaciones <notificaciones@boterosoto.com.co>")]
    [InlineData("notificaciones@intranet")]
    [InlineData(" notificaciones@boterosoto.com.co")]
    [InlineData("\"notificaciones\"@boterosoto.com.co")]
    public void ElRemitenteCorporativoUsaElFormatoEstricto(string remitente)
    {
        var resultado = new clsValidadorOpcionesCorreoTransporte().Validate(null, new clsOpcionesCorreoTransporte
        {
            Habilitado = true,
            MailSender = new clsOpcionesMailSender { UrlBase = "http://mailsender.local", Remitente = remitente },
        });

        resultado.Failed.Should().BeTrue();
        resultado.FailureMessage.Should().Contain("MailSender:Remitente");
    }

    [Theory]
    [InlineData("Development", true, "redirigir", "qa@localhost", false, "Proteccion:BuzonPruebas")]
    [InlineData("Development", true, "redirigir", "qa@pruebas.local", false, null)]
    [InlineData("Staging", true, "redirigir", "qa@pruebas.local", false, null)]
    [InlineData("Staging", true, "ninguno", null, true, "debe ser 'redirigir'")]
    [InlineData("Development", true, "redirigir", null, false, "Proteccion:BuzonPruebas")]
    [InlineData("Development", true, "redirigir", "sin-arroba", false, "Proteccion:BuzonPruebas")]
    [InlineData("Development", true, "redirigir", "qa@pruebas.local,cliente@empresa.com", false, "Proteccion:BuzonPruebas")]
    [InlineData("Development", true, "redirigir", "QA <qa@pruebas.local>", false, "Proteccion:BuzonPruebas")]
    [InlineData("Development", true, null, null, false, "Proteccion:BuzonPruebas")]
    [InlineData("Development", false, "ninguno", null, false, null)]
    [InlineData("Production", true, "ninguno", null, true, null)]
    [InlineData("Production", true, "redirigir", "qa@pruebas.local", false, null)]
    [InlineData("Production", true, "redirigir", null, false, "Proteccion:BuzonPruebas")]
    [InlineData("Production", false, "lista_blanca", null, false, "Proteccion:Modo")]
    // Segura por defecto y sin depender del ambiente (QA y staging corren como Production): el envío real exige las dos
    // llaves; una sola no arranca.
    [InlineData("Production", true, "ninguno", null, false, "PermitirEnvioReal=true")]
    [InlineData("Production", true, "ninguno", "qa@pruebas.local", false, "PermitirEnvioReal=true")]
    [InlineData("Production", true, "redirigir", "qa@pruebas.local", true, "las dos llaves")]
    [InlineData("Production", true, null, "qa@pruebas.local", true, "las dos llaves")]
    [InlineData("Staging", true, "redirigir", "qa@pruebas.local", true, "debe ser 'redirigir'")]
    public void LaProteccionRedirigeSalvoConLasDosLlavesEnProduccion(string entorno, bool habilitado, string? modo, string? buzon, bool permitir, string? error)
    {
        var resultado = new clsValidadorOpcionesCorreo(new clsEntornoFalso(entorno)).Validate(null, new clsOpcionesCorreo
        {
            Habilitado = habilitado,
            Proteccion = new clsOpcionesProteccionCorreo { Modo = modo, BuzonPruebas = buzon, PermitirEnvioReal = permitir },
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

    // El tope de destinatarios es 10 por defecto, configurable de 1 a 100, y se valida aunque el correo
    // esté deshabilitado (la vista previa también lo aplica).
    [Theory]
    [InlineData(10, true, null)]
    [InlineData(1, false, null)]
    [InlineData(100, true, null)]
    [InlineData(0, false, "MaximoDestinatarios")]
    [InlineData(-1, true, "MaximoDestinatarios")]
    [InlineData(101, false, "MaximoDestinatarios")]
    public void ElTopeDeDestinatariosSeValida(int maximo, bool habilitado, string? error)
    {
        var resultado = new clsValidadorOpcionesCorreo(new clsEntornoFalso("Development")).Validate(null, new clsOpcionesCorreo
        {
            Habilitado = habilitado,
            MaximoDestinatarios = maximo,
            Proteccion = new clsOpcionesProteccionCorreo { BuzonPruebas = "qa@pruebas.local" },
        });

        if (error is null)
        {
            resultado.Succeeded.Should().BeTrue(resultado.FailureMessage);
        }
        else
        {
            resultado.Failed.Should().BeTrue();
            resultado.FailureMessage.Should().Contain(error).And.Contain("entre 1 y 100");
        }
    }

    [Fact]
    public void ElTopeSeLeeDeCorreoMaximoDestinatarios()
    {
        using var porDefecto = Proveedor();
        using var configurado = Proveedor(new Dictionary<string, string?> { ["Correo:MaximoDestinatarios"] = "15" });
        using var invalido = Proveedor(new Dictionary<string, string?> { ["Correo:MaximoDestinatarios"] = "0" });

        porDefecto.GetRequiredService<IOptions<clsOpcionesCorreo>>().Value.MaximoDestinatarios.Should().Be(10);
        configurado.GetRequiredService<IOptions<clsOpcionesCorreo>>().Value.MaximoDestinatarios.Should().Be(15);
        var leer = () => invalido.GetRequiredService<IOptions<clsOpcionesCorreo>>().Value;
        leer.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("Correo:MaximoDestinatarios");
    }

    [Fact]
    public void SinBuzonDePruebasLaApiNoArrancaFueraDeProduccion()
    {
        using var proveedor = Proveedor(new Dictionary<string, string?>
        {
            ["Correo:Habilitado"] = "true",
            ["Correo:MailSender:UrlBase"] = clsMailSenderFalso.UrlBase,
        }, entorno: "Staging");

        var leer = () => proveedor.GetRequiredService<IOptions<clsOpcionesCorreo>>().Value;

        leer.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("Proteccion:BuzonPruebas");
    }

    [Fact]
    public void UnaConfiguracionInvalidaImpideArrancar()
    {
        using var proveedor = Proveedor(new Dictionary<string, string?>
        {
            ["Correo:Habilitado"] = "true",
            ["Correo:Modo"] = "smtp_relay",
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
            ["Correo:MailSender:UrlBase"] = clsMailSenderFalso.UrlBase,
            ["Correo:Proteccion:BuzonPruebas"] = clsMailSenderFalso.BuzonPruebas,
            ["Pdf:BaseUrl"] = "https://cotifly.example",
        });

        proveedor.GetRequiredService<IOptions<clsOpcionesCorreo>>().Value.Habilitado.Should().BeTrue();
        proveedor.GetRequiredService<IOptions<clsOpcionesCorreo>>().Value.Proteccion.BuzonPruebas.Should().Be(clsMailSenderFalso.BuzonPruebas);
        proveedor.GetRequiredService<IOptions<clsOpcionesCorreoTransporte>>().Value.MailSender.UrlBase.Should().Be(clsMailSenderFalso.UrlBase);
        proveedor.GetRequiredService<IOptions<clsOpcionesPdf>>().Value.BaseUrl.Should().Be("https://cotifly.example");
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
