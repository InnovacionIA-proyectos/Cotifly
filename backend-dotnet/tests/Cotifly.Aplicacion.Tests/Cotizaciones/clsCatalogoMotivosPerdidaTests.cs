using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Dominio.Comun;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Tests.Cotizaciones;

/// <summary>
/// HU-018.1 (#155, parte B): la lista de motivos de pérdida (por defecto la de Nexo, configurable) y su validación, común a
/// <c>/resultado</c>, <c>/seguimiento</c> y la sincronización diferida.
/// </summary>
public sealed class clsCatalogoMotivosPerdidaTests
{
    private static clsCatalogoMotivosPerdida Catalogo(clsOpcionesResultado? opciones = null) => new(Options.Create(opciones ?? new clsOpcionesResultado()));

    [Fact]
    public void SinConfigurarEsLaListaDeNexoYSoloOtroExigeComentario()
    {
        Catalogo().Listar().Motivos.Should().Equal(
            new clsMotivoPerdida("Valor muy alto", false),
            new clsMotivoPerdida("Flete fuera de rango de plaza", false),
            new clsMotivoPerdida("Cliente desistió del servicio", false),
            new clsMotivoPerdida("Tiempo de respuesta vencido", false),
            new clsMotivoPerdida("Otro", true));
        new clsOpcionesResultado().Errores().Should().BeEmpty();
    }

    [Theory]
    [InlineData("Valor muy alto", null, "Valor muy alto", null, "Valor muy alto")]
    [InlineData("  tiempo de RESPUESTA vencido ", " tarde ", "Tiempo de respuesta vencido", "tarde", "Tiempo de respuesta vencido")]
    [InlineData("OTRO", " Cerró la planta ", "Otro", "Cerró la planta", "Otro: Cerró la planta")]
    public void UnMotivoDeLaListaSeDevuelveConSuEscrituraYElOtroGuardaElComentario(
        string motivo, string? comentario, string esperado, string? comentarioEsperado, string paraCotifly)
    {
        Catalogo().Validar(motivo, comentario).Valor.Should().Be(new clsMotivoPerdidaValidado(esperado, comentarioEsperado, paraCotifly));
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("", null, false)]
    [InlineData("Precio alto", "x", false)]
    [InlineData("Otro", null, true)]
    [InlineData("Otro", "   ", true)]
    public void UnMotivoFueraDeLaListaOOtroSinComentarioEsMotivoNoPermitido(string? motivo, string? comentario, bool requiereComentario)
    {
        var error = Catalogo().Validar(motivo, comentario).Error;

        error.Codigo.Should().Be(clsCodigosError.MotivoNoPermitido);
        error.Datos!["motivos_permitidos"].Should().BeEquivalentTo(clsOpcionesResultado.MotivosPorDefecto);
        error.Datos["requiere_comentario"].Should().Be(requiereComentario);
    }

    [Theory]
    [InlineData("Otro: Cerró la planta", "Otro")]
    [InlineData("otro: algo", "Otro")]
    [InlineData("Valor muy alto", "Valor muy alto")]
    [InlineData("Otro", "Otro")]
    [InlineData("Precio alto", "Precio alto")]
    [InlineData(null, null)]
    public void AlSincronizarANexoVaElMotivoDeLaListaSinElComentario(string? guardado, string? aNexo)
    {
        Catalogo().MotivoParaNexo(guardado).Should().Be(aNexo);
    }

    // Validación HU-018.1 (#155): con Resultado:MotivoConComentario en otra escritura que la lista, a Nexo va la de la lista (la del envío
    // directo), también en la sincronización diferida.
    [Fact]
    public void ConElMotivoConComentarioEnOtraEscrituraANexoVaLaDeLaLista()
    {
        var catalogo = Catalogo(new clsOpcionesResultado { MotivosPerdida = ["Plazo", "Otra razón"], MotivoConComentario = "OTRA RAZÓN" });

        var validado = catalogo.Validar("otra razón", "Se fue").Valor;

        validado.Motivo.Should().Be("Otra razón");
        validado.ParaCotifly.Should().Be("Otra razón: Se fue");
        catalogo.MotivoParaNexo(validado.ParaCotifly).Should().Be("Otra razón", "como en el envío directo");
    }

    // Validación HU-018.1 (#155): la tilde aparte (NFD) es el mismo motivo, en la lista configurada y en lo que llega.
    [Fact]
    public void ElMotivoYLaListaSeComparanEnLaFormaUnicodeCompuesta()
    {
        var nfd = System.Text.NormalizationForm.FormD;
        var catalogo = Catalogo(new clsOpcionesResultado { MotivosPerdida = ["Cliente desistió".Normalize(nfd), "Otro"] });

        catalogo.Listar().Motivos[0].Motivo.Should().Be("Cliente desistió", "la lista se publica en NFC");
        catalogo.Validar("Cliente desistió", null).Valor.Motivo.Should().Be("Cliente desistió");
        Catalogo().Validar("Cliente desistió del servicio".Normalize(nfd), null).Valor.Motivo.Should().Be("Cliente desistió del servicio");
    }

    [Fact]
    public void LaConfiguracionReemplazaLaListaYSeValidaAlArrancar()
    {
        var opciones = new clsOpcionesResultado { MotivosPerdida = [" Plazo ", "", "plazo", "Otra razón"], MotivoConComentario = "otra razón" };

        opciones.Motivos.Should().Equal("Plazo", "Otra razón");
        opciones.Errores().Should().BeEmpty();
        Catalogo(opciones).Listar().Motivos.Should().Equal(new clsMotivoPerdida("Plazo", false), new clsMotivoPerdida("Otra razón", true));

        new clsOpcionesResultado { MotivosPerdida = [new string('m', 101)] }.Errores().Should().HaveCount(2, "el motivo es largo y falta Otro");
        new clsOpcionesResultado { MotivoConComentario = "Nada" }.Errores().Should().ContainSingle();
    }
}
